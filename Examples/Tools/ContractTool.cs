using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The Green Care Plan desk of The Greenhouse &amp; Nursery: the garden-care contract a customer
/// signs alongside their plants — tending visits, coverage, plant-health guarantee, fees, clauses
/// and termination. Reads the same shared database the greenhouse ledger writes (see
/// <see cref="GreenhouseDatabaseHelper"/>): the plan's terms are the shop's, the schedule is the
/// customer's, and the monthly fee it sets is the one BillingTool's invoices charge. Read-only.
/// </summary>
[ProvidesToolForIntent("contract")]
public class ContractTool : MorganaTool
{
    public ContractTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        GreenhouseDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    // The plan is two things at once, and the database keeps them apart: the PRODUCT (one row in
    // PlanProducts with its features, services, clauses, termination steps and documents — the
    // same terms for everyone who signs it) and the SCHEDULE (one row in CarePlans per customer:
    // their contract number, their dates, their fee). Everything below reads that pairing.
    private record CarePlanSchedule(
        string ContractId,
        string UserId,
        string PlanCode,
        DateTime StartDate,
        DateTime EndDate,
        string Status,
        string BillingCycle,
        decimal MonthlyFee,
        string VisitDays);

    private record PlanProduct(
        string PlanCode,
        string Name,
        string VisitFrequency,
        string Coverage,
        string Guarantee,
        decimal MonthlyFee,
        int IncludedVisitsPerMonth,
        decimal ExtraVisitFee,
        int NoticePeriodDays,
        decimal EarlyTerminationFee,
        string RefundPolicy);

    private record PlanClause(
        int ClauseNumber,
        string Title,
        string Summary,
        string FullText,
        string ClauseType);

    private record PlanService(
        string ServiceId,
        string Name,
        string Description,
        decimal MonthlyCost,
        bool IsOptional);

    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string userId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Customers WHERE UserId = $userId COLLATE NOCASE";
        command.Parameters.AddWithValue("$userId", userId);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<CarePlanSchedule?> FindScheduleAsync(SqliteConnection connection, string userId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ContractId, UserId, PlanCode, StartDate, EndDate, Status, BillingCycle, MonthlyFee, VisitDays FROM CarePlans WHERE UserId = $userId COLLATE NOCASE ORDER BY StartDate DESC";
        command.Parameters.AddWithValue("$userId", userId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new CarePlanSchedule(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(5),
            reader.GetString(6),
            (decimal)reader.GetDouble(7),
            reader.GetString(8));
    }

    private static async Task<PlanProduct> GetPlanProductAsync(SqliteConnection connection, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT PlanCode, Name, VisitFrequency, Coverage, Guarantee, MonthlyFee, IncludedVisitsPerMonth, ExtraVisitFee, NoticePeriodDays, EarlyTerminationFee, RefundPolicy FROM PlanProducts WHERE PlanCode = $planCode";
        command.Parameters.AddWithValue("$planCode", planCode);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Plan product '{planCode}' is referenced by a care plan but missing from the database.");

        return new PlanProduct(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            (decimal)reader.GetDouble(5),
            reader.GetInt32(6),
            (decimal)reader.GetDouble(7),
            reader.GetInt32(8),
            (decimal)reader.GetDouble(9),
            reader.GetString(10));
    }

    private static async Task<List<string>> GetTextColumnAsync(SqliteConnection connection, string sql, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$planCode", planCode);

        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));

        return values;
    }

    private static Task<List<string>> GetFeaturesAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Feature FROM PlanFeatures WHERE PlanCode = $planCode ORDER BY Position", planCode);

    private static Task<List<string>> GetTerminationStepsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Description FROM PlanTerminationSteps WHERE PlanCode = $planCode ORDER BY StepNumber", planCode);

    private static Task<List<string>> GetRequiredDocumentsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Document FROM PlanRequiredDocuments WHERE PlanCode = $planCode ORDER BY Position", planCode);

    private static async Task<List<PlanService>> GetServicesAsync(SqliteConnection connection, string planCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ServiceId, Name, Description, MonthlyCost, IsOptional FROM PlanServices WHERE PlanCode = $planCode ORDER BY ServiceId";
        command.Parameters.AddWithValue("$planCode", planCode);

        List<PlanService> services = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            services.Add(new PlanService(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (decimal)reader.GetDouble(3),
                reader.GetInt64(4) != 0));
        }

        return services;
    }

    private static async Task<List<PlanClause>> GetClausesAsync(SqliteConnection connection, string planCode, int? clauseNumber = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = clauseNumber == null
            ? "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode ORDER BY ClauseNumber"
            : "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode AND ClauseNumber = $clauseNumber";
        command.Parameters.AddWithValue("$planCode", planCode);
        if (clauseNumber != null)
            command.Parameters.AddWithValue("$clauseNumber", clauseNumber);

        List<PlanClause> clauses = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            clauses.Add(new PlanClause(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return clauses;
    }

    // No gate on the customer registry, here or anywhere else in this plugin: an unknown code is
    // simply a code no plan hangs from, which is the same answer a known customer without a plan
    // gets. There is nothing to sign anyone up with here either way.
    private static string NoCarePlan(string userId, string? customerName) => JsonSerializer.Serialize(new
    {
        error = "No care plan found",
        userId,
        customerName,
        note = "No Green Care Plan is held under this customer code: either the customer buys from the nursery without one, or the code belongs to another bench or was mistyped. Never invent a code to try again with."
    }, GreenhouseDatabaseHelper.JsonOptions);

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves the customer's Green Care Plan in full as structured JSON.
    /// </summary>
    /// <param name="userId">Customer code (retrieved from context)</param>
    /// <returns>JSON object with complete plan overview</returns>
    public async Task<string> GetContractDetails(string userId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, userId);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, userId);
        if (schedule == null)
            return NoCarePlan(userId, customerName);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> features = await GetFeaturesAsync(connection, schedule.PlanCode);
        List<PlanService> services = await GetServicesAsync(connection, schedule.PlanCode);
        List<PlanClause> clauses = await GetClausesAsync(connection, schedule.PlanCode);

        int remainingDays = (schedule.EndDate - DateTime.UtcNow).Days;

        var result = new
        {
            contractId = schedule.ContractId,
            userId = schedule.UserId,
            customerName,
            status = new
            {
                value = schedule.Status,
                icon = schedule.Status switch
                {
                    "Active" => "✅",
                    "PendingRenewal" => "🔄",
                    "Expired" => "⏰",
                    "Terminated" => "❌",
                    "Suspended" => "⏸️",
                    _ => "📋"
                }
            },
            plan = new
            {
                name = product.Name,
                visitFrequency = product.VisitFrequency,
                coverage = product.Coverage,
                guarantee = product.Guarantee,
                includedFeatures = features
            },
            contractPeriod = new
            {
                startDate = schedule.StartDate.ToString("dd/MM/yyyy"),
                endDate = schedule.EndDate.ToString("dd/MM/yyyy"),
                remainingDays = remainingDays > 0 ? remainingDays : 0,
                remainingMonths = remainingDays > 0 ? remainingDays / 30 : 0
            },
            fee = new
            {
                monthlyFee = schedule.MonthlyFee,
                billingCycle = schedule.BillingCycle
            },
            services = services.Select(service => new
            {
                serviceId = service.ServiceId,
                name = service.Name,
                description = service.Description,
                monthlyCost = service.MonthlyCost,
                isOptional = service.IsOptional,
                category = service.IsOptional ? "Optional" : "Required"
            }).ToList(),
            termination = new
            {
                noticePeriodDays = product.NoticePeriodDays,
                earlyTerminationFee = product.EarlyTerminationFee,
                autoRenewal = new
                {
                    enabled = true,
                    noticeDays = 60,
                    renewalDate = schedule.EndDate.ToString("dd/MM/yyyy")
                }
            },
            availableClauses = clauses.Select(clause => new
            {
                clauseNumber = clause.ClauseNumber,
                title = clause.Title,
                type = clause.ClauseType
            }).ToList()
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves a single clause of the customer's Green Care Plan as structured JSON.
    /// </summary>
    /// <param name="userId">Customer code (retrieved from context)</param>
    /// <param name="clauseNumber">Clause number to retrieve (1-7)</param>
    /// <returns>JSON object with complete clause details</returns>
    public async Task<string> GetContractClause(string userId, int clauseNumber)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, userId);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, userId);
        if (schedule == null)
            return NoCarePlan(userId, customerName);

        List<PlanClause> clauses = await GetClausesAsync(connection, schedule.PlanCode, clauseNumber);
        if (clauses.Count == 0)
        {
            List<PlanClause> allClauses = await GetClausesAsync(connection, schedule.PlanCode);
            return JsonSerializer.Serialize(new
            {
                error = "Clause not found",
                requestedClauseNumber = clauseNumber,
                availableClauses = allClauses.Select(clause => new
                {
                    clauseNumber = clause.ClauseNumber,
                    title = clause.Title
                }).ToList()
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        PlanClause found = clauses[0];

        var result = new
        {
            contractId = schedule.ContractId,
            clauseNumber = found.ClauseNumber,
            title = found.Title,
            type = found.ClauseType,
            summary = found.Summary,
            fullText = found.FullText,
            relatedInfo = found.ClauseType switch
            {
                "Termination" => "For termination procedures, use GetTerminationProcedure tool",
                "VisitSchedule" => "Extra visits are charged on the customer's invoices, which another bench of the nursery keeps: no tool here reads them",
                "PlantHealth" => "For the dates this guarantee runs against, use GetContractDetails tool",
                _ => null
            }
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the customer's tending calendar: the visits already made, and when the next ones fall.
    /// </summary>
    /// <param name="userId">Customer code (retrieved from context)</param>
    /// <returns>JSON object with the recent visits, the upcoming dates and this month's allowance</returns>
    public async Task<string> GetVisitSchedule(string userId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, userId);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, userId);
        if (schedule == null)
            return NoCarePlan(userId, customerName);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        DateTime today = DateTime.UtcNow.Date;

        // Only visits that have actually happened are read from the table. What is still to come is
        // COMPUTED from the plan's own visit days, never stored: a seeded calendar of future dates
        // is stale the day after it is written, while a recurrence rule is right forever.
        List<object> recent = [];
        int takenThisMonth = 0;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT VisitId, VisitDate, Kind, Outcome, Notes, InvoiceId FROM PlanVisits WHERE ContractId = $contractId AND VisitDate <= $today ORDER BY VisitDate DESC LIMIT 8";
            command.Parameters.AddWithValue("$contractId", schedule.ContractId);
            command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                DateTime visitDate = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                string kind = reader.GetString(2);
                string outcome = reader.GetString(3);

                if (visitDate.Year == today.Year && visitDate.Month == today.Month && kind == "Included" && outcome == "Completed")
                    takenThisMonth++;

                recent.Add(new
                {
                    visitId = reader.GetString(0),
                    date = visitDate.ToString("dd/MM/yyyy"),
                    kind,
                    outcome,
                    outcomeIcon = outcome == "Completed" ? "\u2705" : "\u26A0\uFE0F",
                    notes = reader.GetString(4),
                    charged = kind == "Extra",
                    invoiceId = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        var result = new
        {
            contractId = schedule.ContractId,
            customerName,
            visitFrequency = product.VisitFrequency,
            visitDays = schedule.VisitDays,
            allowance = new
            {
                includedPerMonth = product.IncludedVisitsPerMonth,
                takenThisMonth,
                remainingThisMonth = Math.Max(0, product.IncludedVisitsPerMonth - takenThisMonth),
                extraVisitFee = product.ExtraVisitFee
            },
            upcoming = NextVisitDates(schedule.VisitDays, today, 3).Select(date => new
            {
                date = date.ToString("dd/MM/yyyy"),
                daysAway = (date - today).Days,
                kind = "Included"
            }).ToList(),
            recentVisits = recent
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// The next <paramref name="count"/> occurrences of the contract's visit days, strictly after
    /// <paramref name="today"/>.
    /// </summary>
    /// <remarks>
    /// A day the month is too short for is simply skipped rather than clamped: a plan tended on the
    /// 30th is not tended on the 28th of February just because the calendar is shorter.
    /// </remarks>
    private static List<DateTime> NextVisitDates(string visitDays, DateTime today, int count)
    {
        int[] days = visitDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .OrderBy(day => day)
            .ToArray();

        List<DateTime> dates = [];
        DateTime month = new DateTime(today.Year, today.Month, 1);

        while (dates.Count < count)
        {
            foreach (int day in days)
            {
                if (day > DateTime.DaysInMonth(month.Year, month.Month))
                    continue;

                DateTime candidate = new DateTime(month.Year, month.Month, day);
                if (candidate > today && dates.Count < count)
                    dates.Add(candidate);
            }

            month = month.AddMonths(1);
        }

        return dates;
    }

    /// <summary>
    /// Provides the step-by-step termination procedure of the customer's plan as structured JSON.
    /// </summary>
    /// <param name="userId">Customer code (retrieved from context)</param>
    /// <param name="reason">Optional termination reason for internal tracking</param>
    /// <returns>JSON object with complete termination guide</returns>
    public async Task<string> GetTerminationProcedure(string userId, string? reason = null)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, userId);

        CarePlanSchedule? schedule = await FindScheduleAsync(connection, userId);
        if (schedule == null)
            return NoCarePlan(userId, customerName);

        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> steps = await GetTerminationStepsAsync(connection, schedule.PlanCode);
        List<string> documents = await GetRequiredDocumentsAsync(connection, schedule.PlanCode);

        DateTime earliestTerminationDate = DateTime.UtcNow.AddDays(product.NoticePeriodDays);
        bool earlyTermination = earliestTerminationDate < schedule.EndDate;

        var result = new
        {
            contractId = schedule.ContractId,
            customerName,
            reason = reason ?? "Not specified",
            noticePeriod = new
            {
                requiredDays = product.NoticePeriodDays,
                earliestEffectiveDate = earliestTerminationDate.ToString("dd/MM/yyyy")
            },
            fees = new
            {
                earlyTermination = new
                {
                    applicable = earlyTermination,
                    amount = earlyTermination ? product.EarlyTerminationFee : 0m,
                    reason = earlyTermination
                        ? $"Plan runs to {schedule.EndDate:dd/MM/yyyy}, termination before this date incurs fee"
                        : "No early termination fee (plan expired or within normal period)"
                },
                waiverEligibility = new
                {
                    available = true,
                    conditions = new[]
                    {
                        "Relocation outside the service area (proof required)",
                        "Three consecutive months with more than half the scheduled visits missed by the nursery"
                    }
                }
            },
            procedure = new
            {
                steps = steps.Select((step, index) => new
                {
                    stepNumber = index + 1,
                    description = step
                }).ToList(),
                requiredDocuments = documents
            },
            refundPolicy = product.RefundPolicy,
            importantNotes = new[]
            {
                "Termination request must be submitted in writing",
                "All outstanding invoices must be settled before termination",
                "Leased equipment must be returned to avoid replacement charges",
                "The plant health guarantee lapses on the termination effective date"
            }
        };

        return JsonSerializer.Serialize(result, GreenhouseDatabaseHelper.JsonOptions);
    }
}

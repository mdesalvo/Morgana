using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Tools;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The Green Care Plan agent of The Greenhouse &amp; Nursery: the garden-care contract a customer signs beside their plants.
/// The plan's terms are the shop's and the schedule is the customer's; it only reads, except for enrolling a customer in a new plan,
/// which bills through <see cref="ExamplesDatabaseHelper.BillCustomerAsync"/> as a confirmed order does.
/// </summary>
[ProvidesToolForIntent("contract")]
public class ContractTool : MorganaTool
{
    /// <summary>Binds the tools to the agent's context and makes sure the shop's database is deployed.</summary>
    public ContractTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        // The first tool built in the process deploys the seed; every later one finds it in place.
        ExamplesDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    /// <summary>
    /// The customer's side of a plan: one row in CarePlans with the contract number, the dates and the fee.
    /// The plan's product, the same terms for everyone who signs it, sits apart in PlanProducts.
    /// </summary>
    private record CarePlanSchedule(
        string ContractId,
        string CustomerCode,
        string PlanCode,
        DateTime StartDate,
        DateTime EndDate,
        string Status,
        string BillingCycle,
        decimal MonthlyFee,
        string VisitDays);

    /// <summary>The plan as the shop offers it, the same terms for everyone who signs it.</summary>
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

    /// <summary>One clause of the plan, with its summary and its binding wording.</summary>
    private record PlanClause(
        int ClauseNumber,
        string Title,
        string Summary,
        string FullText,
        string ClauseType);

    /// <summary>One service of the plan, included or optional.</summary>
    private record PlanService(
        string ServiceId,
        string Name,
        string Description,
        decimal MonthlyCost,
        bool IsOptional);

    /// <summary>Finds the customer's most recent plan; null when the code holds none.</summary>
    private static async Task<CarePlanSchedule?> FindScheduleAsync(SqliteConnection connection, string customerCode)
    {
        // The customer's plans, the latest started first, the code matched ignoring case.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ContractId, CustomerCode, PlanCode, StartDate, EndDate, Status, BillingCycle, MonthlyFee, VisitDays FROM CarePlans WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY StartDate DESC";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        // A code no plan hangs from has no schedule.
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        // The latest plan, its dates read back as they were stored.
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

    /// <summary>Reads the plan product that a schedule refers to.</summary>
    private static async Task<PlanProduct> GetPlanProductAsync(SqliteConnection connection, string planCode)
    {
        // One row per product, by its code.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT PlanCode, Name, VisitFrequency, Coverage, Guarantee, MonthlyFee, IncludedVisitsPerMonth, ExtraVisitFee, NoticePeriodDays, EarlyTerminationFee, RefundPolicy FROM PlanProducts WHERE PlanCode = $planCode";
        command.Parameters.AddWithValue("$planCode", planCode);

        // A schedule pointing at a product that is not there means the seed is broken.
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Plan product '{planCode}' is referenced by a care plan but missing from the database.");

        // The product with its fees and its terms.
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

    /// <summary>Reads the single text column that a query of one plan returns, in its order.</summary>
    private static async Task<List<string>> GetTextColumnAsync(SqliteConnection connection, string sql, string planCode)
    {
        // The query names the plan by its code.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$planCode", planCode);

        // One value per row.
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));

        // The values in the order the query sets.
        return values;
    }

    /// <summary>Reads the features the plan includes, in the shop's order.</summary>
    private static Task<List<string>> GetFeaturesAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Feature FROM PlanFeatures WHERE PlanCode = $planCode ORDER BY Position", planCode);

    /// <summary>Reads the steps of ending the plan, in their order.</summary>
    private static Task<List<string>> GetTerminationStepsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Description FROM PlanTerminationSteps WHERE PlanCode = $planCode ORDER BY StepNumber", planCode);

    /// <summary>Reads the documents that ending the plan requires.</summary>
    private static Task<List<string>> GetRequiredDocumentsAsync(SqliteConnection connection, string planCode) =>
        GetTextColumnAsync(connection, "SELECT Document FROM PlanRequiredDocuments WHERE PlanCode = $planCode ORDER BY Position", planCode);

    /// <summary>Reads the services of the plan, included and optional.</summary>
    private static async Task<List<PlanService>> GetServicesAsync(SqliteConnection connection, string planCode)
    {
        // The services of the plan by their identifier.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ServiceId, Name, Description, MonthlyCost, IsOptional FROM PlanServices WHERE PlanCode = $planCode ORDER BY ServiceId";
        command.Parameters.AddWithValue("$planCode", planCode);

        // One service per row, the optional flag stored as an integer.
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

        // The plan's services.
        return services;
    }

    /// <summary>Reads the clauses of the plan or the one clause with the given number.</summary>
    private static async Task<List<PlanClause>> GetClausesAsync(SqliteConnection connection, string planCode, int? clauseNumber = null)
    {
        // Every clause in order or the one asked for.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = clauseNumber == null
            ? "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode ORDER BY ClauseNumber"
            : "SELECT ClauseNumber, Title, Summary, FullText, ClauseType FROM PlanClauses WHERE PlanCode = $planCode AND ClauseNumber = $clauseNumber";
        command.Parameters.AddWithValue("$planCode", planCode);
        if (clauseNumber != null)
            command.Parameters.AddWithValue("$clauseNumber", clauseNumber);

        // One clause per row.
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

        // The clauses found, none when the number names no clause.
        return clauses;
    }

    /// <summary>
    /// The answer for a code no plan hangs from, whether the customer is known or not.
    /// No gate on the customer registry, here or anywhere in this plugin: SubscribeToGreenCarePlan takes either code the same way.
    /// </summary>
    private static string NoCarePlan(string customerCode) => JsonSerializer.Serialize(new
    {
        error = "No care plan found",
        customerCode,
        note = "No Green Care Plan is held under this customer code: either none was ever opened under it, or the code is mistyped. This response carries nothing about what the plan offers or costs — no term, fee or figure of the plan can be read out of it. SubscribeToGreenCarePlan is the only tool that opens one."
    }, ExamplesDatabaseHelper.JsonOptions);

    /// <summary>The only plan product the nursery offers; a second one would turn this constant into a parameter and nothing else.</summary>
    private const string DefaultPlanCode = "GREEN-CARE-PREMIUM";

    /// <summary>
    /// Spaces the visit days of a month as evenly as a 28-day month allows, anchored on the enrollment date.
    /// The same shape as the seed data, such as "6,20", fourteen days apart.
    /// </summary>
    private static string PickVisitDays(DateTime startDate, int count)
    {
        // A plan visits at least once a month, every visit day the same distance from the next.
        count = Math.Max(1, count);
        int step = 28 / count;

        // The enrollment day opens the cycle, kept within the 28 days every month has.
        int firstDay = Math.Clamp(startDate.Day, 1, 28);

        // The days as the schedule stores them, comma-separated.
        return string.Join(',', Enumerable.Range(0, count).Select(position => ((firstDay - 1 + position * step) % 28) + 1));
    }

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Enrolls a customer in the Green Care Plan: opens a new schedule and bills the first month's fee with it, in one transaction.
    /// The fee goes through the backoffice write path that a confirmed order is billed through.
    /// </summary>
    [Description("Enrolls a customer in the Green Care Plan: opens a new contract and bills its first month's fee in the same action. Returns structured JSON: contractId, planCode, planName, status ('Active'), startDate, endDate, monthlyFee, visitDays, invoiceId, note. invoiceId identifies the invoice this enrollment was just billed to: it is the invoice's identifier and nothing else about it. Fails with an error and the existingContractId if the customer already holds an Active or PendingRenewal plan — no code is ever rejected for being unregistered. This IS a dispositive, binding action — you must have asked the user for explicit confirmation before calling it. This tool has dispositive capabilities: it commits a real, persistent change to the shop's books and to the customer's account.")]
    public async Task<string> SubscribeToGreenCarePlan(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // The plans are part of the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // One active or renewing plan per customer at a time: a rule of what a Green Care Plan is,
        // not a gate on whether the customer code is real.
        CarePlanSchedule? existingSchedule = await FindScheduleAsync(connection, customerCode);
        if (existingSchedule != null && existingSchedule.Status is "Active" or "PendingRenewal")
        {
            return JsonSerializer.Serialize(new
            {
                error = "Customer already has an active Green Care Plan",
                existingContractId = existingSchedule.ContractId,
                status = existingSchedule.Status,
                note = "Only one Green Care Plan may be active per customer code at a time and this code already holds one: nothing was opened and nothing was billed. GetContractDetails reads the existing plan, GetTerminationProcedure describes how one is ended."
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The plan the nursery offers, whose fee and visits the new schedule takes.
        PlanProduct product = await GetPlanProductAsync(connection, DefaultPlanCode);

        // A year from today, under a short random contract number, on visit days anchored on today.
        DateTime startDate = DateTime.UtcNow.Date;
        DateTime endDate = startDate.AddMonths(12);
        string contractId = $"GCP-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        string visitDays = PickVisitDays(startDate, product.IncludedVisitsPerMonth);

        // The schedule and its first invoice commit together or not at all.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // The new schedule starts active and billed monthly.
        await using (SqliteCommand insertPlan = connection.CreateCommand())
        {
            insertPlan.Transaction = transaction;
            insertPlan.CommandText = """
                INSERT INTO CarePlans (ContractId, CustomerCode, PlanCode, StartDate, EndDate, Status, BillingCycle, MonthlyFee, VisitDays)
                VALUES ($contractId, $customerCode, $planCode, $startDate, $endDate, 'Active', 'Monthly', $monthlyFee, $visitDays)
                """;
            insertPlan.Parameters.AddWithValue("$contractId", contractId);
            insertPlan.Parameters.AddWithValue("$customerCode", customerCode);
            insertPlan.Parameters.AddWithValue("$planCode", DefaultPlanCode);
            insertPlan.Parameters.AddWithValue("$startDate", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertPlan.Parameters.AddWithValue("$endDate", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertPlan.Parameters.AddWithValue("$monthlyFee", (double)product.MonthlyFee);
            insertPlan.Parameters.AddWithValue("$visitDays", visitDays);
            await insertPlan.ExecuteNonQueryAsync();
        }

        // The first month's fee joins the customer's open invoice of the month or opens one.
        string invoiceId = await ExamplesDatabaseHelper.BillCustomerAsync(connection, transaction, customerCode,
            $"{product.Name} - Monthly Fee", null, null, (double)product.MonthlyFee, 1, startDate);

        // The enrollment becomes real.
        await transaction.CommitAsync();

        toolLogger.LogInformation("Enrolled {CustomerCode} in Green Care Plan {PlanCode}: contract {ContractId}, billed to invoice {InvoiceId}", customerCode, DefaultPlanCode, contractId, invoiceId);

        // The new plan with the invoice it was billed to, which is all this response says about that invoice.
        return JsonSerializer.Serialize(new
        {
            contractId,
            customerCode,
            planCode = DefaultPlanCode,
            planName = product.Name,
            status = "Active",
            startDate = startDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            endDate = endDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            monthlyFee = product.MonthlyFee,
            visitDays,
            invoiceId,
            note = "The plan is active and its first month has been billed to this invoice. This response carries the invoice identifier and nothing else about it: its total and its line items are not on these pages."
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the Green Care Plan's own terms as structured JSON, with no customer code and no schedule required.
    /// Every other read tool here needs a schedule, which a prospect does not have: this is what the confirmation before enrolling grounds its figures in.
    /// </summary>
    [Description("Retrieves the Green Care Plan's own terms as structured JSON — planCode, name, visitFrequency, coverage, guarantee, monthlyFee, includedFeatures, noticePeriodDays, earlyTerminationFee. Needs no customer code and no existing schedule: this is the ONE tool that answers what the plan itself offers and costs, for a prospect who does not hold one yet as much as for a customer who already does. Never answer a 'what does it include / what does it cost' question from GetContractDetails, from memory, or from something you said earlier in this conversation — always call this tool first.")]
    public async Task<string> GetPlanOverview()
    {
        // The plans are part of the shop's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The plan the nursery offers with what it includes.
        PlanProduct product = await GetPlanProductAsync(connection, DefaultPlanCode);
        List<string> features = await GetFeaturesAsync(connection, DefaultPlanCode);

        // The terms a prospect weighs before signing.
        return JsonSerializer.Serialize(new
        {
            planCode = product.PlanCode,
            name = product.Name,
            visitFrequency = product.VisitFrequency,
            coverage = product.Coverage,
            guarantee = product.Guarantee,
            monthlyFee = product.MonthlyFee,
            includedFeatures = features,
            noticePeriodDays = product.NoticePeriodDays,
            earlyTerminationFee = product.EarlyTerminationFee
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the customer's Green Care Plan in full as structured JSON.</summary>
    [Description("Retrieves comprehensive Green Care Plan information for that customer as structured JSON including: contractId, status (with icon), plan details (name, visit frequency, garden coverage, plant-health guarantee, included features), plan period (with remaining days/months), fee (amount and cycle), optional services array, termination basics and available clauses list. This tool has only informative capabilities: you will NOT find here dispositive actions on an EXISTING plan (e.g: 'Change plan', 'Book a visit', 'Terminate plan', ...). A code no plan hangs from is reported as having none — that is an answer, not a failure; enrolling one is SubscribeToGreenCarePlan's job, not this tool's and only once the customer asks.")]
    public async Task<string> GetContractDetails(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // The plans are part of the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // A code no plan hangs from is answered as such.
        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return NoCarePlan(customerCode);

        // The plan's product with everything it includes and states.
        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> features = await GetFeaturesAsync(connection, schedule.PlanCode);
        List<PlanService> services = await GetServicesAsync(connection, schedule.PlanCode);
        List<PlanClause> clauses = await GetClausesAsync(connection, schedule.PlanCode);

        // The days left on the contract, negative once it has ended.
        int remainingDays = (schedule.EndDate - DateTime.UtcNow).Days;

        // The whole plan: status, product, period, fee, services, termination terms and the clauses' titles.
        object result = new
        {
            contractId = schedule.ContractId,
            customerCode = schedule.CustomerCode,
            status = new
            {
                value = schedule.Status,
                // The icon of each status; an unknown one shows a generic icon.
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
                startDate = schedule.StartDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                endDate = schedule.EndDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
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
                    renewalDate = schedule.EndDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                }
            },
            availableClauses = clauses.Select(clause => new
            {
                clauseNumber = clause.ClauseNumber,
                title = clause.Title,
                type = clause.ClauseType
            }).ToList()
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves one clause of the Green Care Plan, the same for every customer, as structured JSON.</summary>
    [Description("Retrieves a specific plan clause as structured JSON including: clauseNumber, title, type, summary, fullText and relatedInfo. The plan has 7 clauses: (1) Plant Health Guarantee, (2) Payment Terms, (3) Visit Schedule and Rescheduling, (4) Termination Terms, (5) Auto-Renewal, (6) Limitation of Liability, (7) Data Privacy (GDPR). The clauses are the plan's own terms, identical for everyone who signs it; the dates and fees specific to this customer are in GetContractDetails. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Modify clause', ...).")]
    public async Task<string> GetContractClause(
        [Description("Clause number to retrieve (1-7). The user should specify which clause they want to read, or you should present the available clause topics and ask them to choose. Do NOT guess or assume which clause the user wants.")] [ToolParameter(Records.ToolScope.Request)] int clauseNumber)
    {
        // The clauses sit in the shop's one system of record beside every other agent's data.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // The clauses belong to the plan and not to a customer's contract: the plan's own code reads them
        // so a prospect and a customer are shown the same wording.
        List<PlanClause> clauses = await GetClausesAsync(connection, DefaultPlanCode, clauseNumber);

        // No clause of the plan carries the number that was asked for.
        if (clauses.Count == 0)
        {
            // The clauses that exist let the caller pick a valid number instead of guessing again.
            List<PlanClause> allClauses = await GetClausesAsync(connection, DefaultPlanCode);

            // Delivers the refusal together with the numbers and titles that can be asked for.
            return JsonSerializer.Serialize(new
            {
                error = "Clause not found",
                requestedClauseNumber = clauseNumber,
                availableClauses = allClauses.Select(clause => new
                {
                    clauseNumber = clause.ClauseNumber,
                    title = clause.Title
                }).ToList()
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // A clause number is unique within the plan so the one row is the clause that was asked for.
        PlanClause requestedClause = clauses[0];

        // Holds the binding wording of the clause with its summary and where related matters are read.
        object result = new
        {
            clauseNumber = requestedClause.ClauseNumber,
            title = requestedClause.Title,
            type = requestedClause.ClauseType,
            summary = requestedClause.Summary,
            fullText = requestedClause.FullText,
            // Points each clause type to where its related matter is read; any other type points nowhere.
            relatedInfo = requestedClause.ClauseType switch
            {
                "Termination" => "For termination procedures, use GetTerminationProcedure tool",
                "VisitSchedule" => "Extra visits are charged on the customer's invoices, which another bench of the nursery keeps: no tool here reads them",
                "PlantHealth" => "For the dates this guarantee runs against, use GetContractDetails tool",
                _ => null
            }
        };

        // Delivers the clause to the caller as JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Describes, step by step, how the customer's plan is ended, with its notice and its fees, as structured JSON.</summary>
    [Description("Retrieves the plan termination procedure as structured JSON including: contractId, noticePeriod (requiredDays, earliestEffectiveDate), fees (earlyTermination amount and applicability, waiverEligibility), procedure (steps array, requiredDocuments array), refundPolicy and importantNotes. This tool has only informative capabilities: you will NOT find here dispositive actions (e.g: 'Terminate plan', ...) — it explains the steps, it does not take them.")]
    public async Task<string> GetTerminationProcedure(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("Optional reason for considering termination (e.g., 'moving house', 'tending the garden myself', 'unhappy with the visits'). This is recorded for internal purposes but is not required. Ask the user if they'd like to provide a reason, but make it clear it's optional.")] [ToolParameter(Records.ToolScope.Request)] string? reason = null)
    {
        // The plans are part of the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // A code no plan hangs from is answered as such.
        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return NoCarePlan(customerCode);

        // The plan's notice, fees, steps and documents.
        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        List<string> steps = await GetTerminationStepsAsync(connection, schedule.PlanCode);
        List<string> documents = await GetRequiredDocumentsAsync(connection, schedule.PlanCode);

        // The plan ends no sooner than the notice allows; ending it before its own end date costs the early fee.
        DateTime earliestTerminationDate = DateTime.UtcNow.AddDays(product.NoticePeriodDays);
        bool earlyTermination = earliestTerminationDate < schedule.EndDate;

        // The whole procedure with the fees, the waivers and the notes that bind it.
        object result = new
        {
            contractId = schedule.ContractId,
            reason = reason ?? "Not specified",
            noticePeriod = new
            {
                requiredDays = product.NoticePeriodDays,
                earliestEffectiveDate = earliestTerminationDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
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

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the customer's tending calendar: the visits already made and when the next ones fall.</summary>
    [Description("Retrieves the customer's tending calendar as structured JSON: visitFrequency, visitDays, allowance (includedPerMonth, takenThisMonth, remainingThisMonth, extraVisitFee), upcoming (the next dates, computed from the plan's own visit days) and recentVisits (date, kind Included/Extra, outcome Completed/Missed, notes and — where an extra visit was charged — the invoiceId it appears on). This tool has only informative capabilities: it does NOT book, move or cancel a visit and no tool here does.")]
    public async Task<string> GetVisitSchedule(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // The plans are part of the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // A code no plan hangs from is answered as such.
        CarePlanSchedule? schedule = await FindScheduleAsync(connection, customerCode);
        if (schedule == null)
            return NoCarePlan(customerCode);

        // The plan's allowance of visits, measured against today.
        PlanProduct product = await GetPlanProductAsync(connection, schedule.PlanCode);
        DateTime today = DateTime.UtcNow.Date;

        // Only visits that have actually happened are read from the table. What is still to come is
        // computed from the plan's own visit days, never stored: a seeded calendar of future dates
        // is stale the day after it is written, while a recurrence rule is right forever.
        List<object> recent = [];
        int takenThisMonth = 0;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            // The last eight visits up to today, most recent first.
            command.CommandText = "SELECT VisitId, VisitDate, Kind, Outcome, Notes, InvoiceId FROM PlanVisits WHERE ContractId = $contractId AND VisitDate <= $today ORDER BY VisitDate DESC LIMIT 8";
            command.Parameters.AddWithValue("$contractId", schedule.ContractId);
            command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // The visit's date, whether it was included or extra and how it went.
                DateTime visitDate = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                string kind = reader.GetString(2);
                string outcome = reader.GetString(3);

                // An included visit completed this month uses up one of the month's allowance.
                if (visitDate.Year == today.Year && visitDate.Month == today.Month && kind == "Included" && outcome == "Completed")
                    takenThisMonth++;

                // The visit as the customer reads it; an extra visit is charged on an invoice.
                recent.Add(new
                {
                    visitId = reader.GetString(0),
                    date = visitDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    kind,
                    outcome,
                    outcomeIcon = outcome == "Completed" ? "\u2705" : "\u26A0\uFE0F",
                    notes = reader.GetString(4),
                    charged = kind == "Extra",
                    invoiceId = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        // The allowance, the next three visit dates and the recent visits.
        object result = new
        {
            contractId = schedule.ContractId,
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
                date = date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                daysAway = (date - today).Days,
                kind = "Included"
            }).ToList(),
            recentVisits = recent
        };

        // The model reads the result as compact camelCase JSON.
        return JsonSerializer.Serialize(result, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Returns the next occurrences of the contract's visit days, strictly after today.
    /// A day the month is too short for is skipped rather than clamped: a plan tended on the 30th is not tended on the 28th of February.
    /// </summary>
    private static List<DateTime> NextVisitDates(string visitDays, DateTime today, int count)
    {
        // The visit days of a month, in order.
        int[] days = visitDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .OrderBy(day => day)
            .ToArray();

        // The search starts on the current month.
        List<DateTime> dates = [];
        DateTime month = new DateTime(today.Year, today.Month, 1);

        // Month after month until enough dates are found.
        while (dates.Count < count)
        {
            foreach (int day in days)
            {
                // A day the month does not have is skipped.
                if (day > DateTime.DaysInMonth(month.Year, month.Month))
                    continue;

                // A day still to come is a visit date.
                DateTime candidate = new DateTime(month.Year, month.Month, day);
                if (candidate > today && dates.Count < count)
                    dates.Add(candidate);
            }

            // The next month carries the search on.
            month = month.AddMonths(1);
        }

        // The upcoming visit dates in order.
        return dates;
    }
}
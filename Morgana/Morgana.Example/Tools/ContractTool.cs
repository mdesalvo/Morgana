using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Attributes;
using Morgana.Framework.Providers;
using System.Text.Json;

namespace Morgana.Example.Tools;

/// <summary>
/// Professional contract management tool with structured contract details, clauses, and termination procedures.
/// Provides realistic telecom contract information with service tiers, terms, and legal clauses.
/// </summary>
[ProvidesToolForIntent("contract")]
public class ContractTool : MorganaTool
{
    public ContractTool(
        ILogger toolLogger,
        Func<MorganaAIContextProvider> getContextProvider) : base(toolLogger, getContextProvider) { }

    // =========================================================================
    // DOMAIN MODELS
    // =========================================================================

    public record Contract(
        string ContractId,
        string UserId,
        ServicePlan Plan,
        DateTime StartDate,
        DateTime EndDate,
        ContractStatus Status,
        decimal MonthlyFee,
        BillingCycle BillingCycle,
        List<ContractClause> Clauses,
        List<ActiveService> Services,
        TerminationPolicy Termination);

    public record ServicePlan(
        string Name,
        string Speed,
        string DataLimit,
        string ServiceLevel,
        List<string> IncludedFeatures);

    public enum ContractStatus
    {
        Active,
        PendingRenewal,
        Expired,
        Terminated,
        Suspended
    }

    public enum BillingCycle
    {
        Monthly,
        Quarterly,
        Annual
    }

    public record ContractClause(
        int ClauseNumber,
        string Title,
        string Summary,
        string FullText,
        ClauseType Type);

    public enum ClauseType
    {
        ServiceLevel,
        Payment,
        DataUsage,
        Termination,
        Liability,
        Privacy,
        RenewalTerms
    }

    public record ActiveService(
        string ServiceId,
        string Name,
        string Description,
        decimal MonthlyCost,
        bool IsOptional);

    public record TerminationPolicy(
        int NoticePeriodDays,
        decimal EarlyTerminationFee,
        List<string> TerminationSteps,
        List<string> RequiredDocuments,
        string RefundPolicy);

    // =========================================================================
    // MOCK DATA
    // =========================================================================

    private readonly Contract _mockContract = new Contract(
        ContractId: "CTR-2025-P994E",
        UserId: "P994E",
        Plan: new ServicePlan(
            Name: "Premium 100Mbps Business",
            Speed: "100 Mbps download / 20 Mbps upload",
            DataLimit: "250 GB per month",
            ServiceLevel: "99.5% uptime guarantee",
            IncludedFeatures:
            [
                "Dedicated fiber connection",
                "Static IP address",
                "24/7 priority support",
                "Business-grade router (free rental)",
                "Free installation and setup",
                "Monthly usage reports"
            ]),
        StartDate: new DateTime(2025, 1, 1),
        EndDate: new DateTime(2025, 12, 31),
        Status: ContractStatus.Active,
        MonthlyFee: 150.00m,
        BillingCycle: BillingCycle.Monthly,
        Clauses:
        [
            new ContractClause(
                ClauseNumber: 1,
                Title: "Service Level Agreement (SLA)",
                Summary: "Guarantees 99.5% uptime with compensation for extended outages.",
                FullText: "The Service Provider guarantees a minimum uptime of 99.5% measured monthly. " +
                         "In the event of service disruption exceeding 0.5% of monthly uptime (approximately 3.6 hours), " +
                         "the Customer is entitled to service credit equal to one day's pro-rata monthly fee per hour of additional downtime. " +
                         "Planned maintenance windows (notified 48 hours in advance) are excluded from SLA calculations. " +
                         "Maximum monthly credit capped at 30% of monthly subscription fee.",
                Type: ClauseType.ServiceLevel),

            new ContractClause(
                ClauseNumber: 2,
                Title: "Payment Terms",
                Summary: "Monthly billing cycle with 15-day payment window and late fee provisions.",
                FullText: "Invoices are issued on the 1st business day of each month and are due within 15 calendar days. " +
                         "Payment methods accepted: bank transfer, credit card, direct debit. " +
                         "Late payments incur a 2% monthly interest charge on outstanding balance. " +
                         "Service suspension occurs after 30 days of non-payment with 7-day advance notice. " +
                         "Reconnection fee of €50 applies if service is suspended for non-payment.",
                Type: ClauseType.Payment),

            new ContractClause(
                ClauseNumber: 3,
                Title: "Data Usage Policy",
                Summary: "250 GB monthly data cap with overage charges of €0.02/MB beyond limit.",
                FullText: "The service plan includes 250 GB of monthly data transfer. " +
                         "Data usage resets on the 1st of each month. " +
                         "Overage charges apply at €0.02 per megabyte beyond the included limit. " +
                         "Customers receive notification at 80% and 95% usage thresholds. " +
                         "Fair usage policy prohibits illegal activities, spam distribution, or excessive bandwidth consumption affecting network performance.",
                Type: ClauseType.DataUsage),

            new ContractClause(
                ClauseNumber: 4,
                Title: "Termination Terms",
                Summary: "30-day notice required; €300 early termination fee if cancelled before contract end.",
                FullText: "Either party may terminate this contract with 30 days written notice. " +
                         "Early termination (before December 31, 2025) incurs a fee of €300.00. " +
                         "Termination requests must be submitted via customer portal or registered mail. " +
                         "Equipment rentals must be returned within 30 days of termination; unreturned equipment charged at replacement value (€150 for router). " +
                         "Pro-rata refunds issued for unused service period (minimum 7 days). " +
                         "Early termination fee waived for: (1) relocation outside service area, (2) documented service failures exceeding SLA for 3 consecutive months.",
                Type: ClauseType.Termination),

            new ContractClause(
                ClauseNumber: 5,
                Title: "Auto-Renewal Terms",
                Summary: "Contract automatically renews for 12 months unless cancelled 60 days before expiry.",
                FullText: "This contract automatically renews for successive 12-month periods unless either party provides written termination notice at least 60 days before the current term's expiration date. " +
                         "Renewal terms and pricing may be adjusted with 90 days advance notice. " +
                         "Customers will receive renewal reminder notifications 90 and 60 days before expiration. " +
                         "Post-renewal cancellations are subject to standard 30-day notice period but no early termination fee applies.",
                Type: ClauseType.RenewalTerms),

            new ContractClause(
                ClauseNumber: 6,
                Title: "Limitation of Liability",
                Summary: "Provider liability limited to 3 months service fees; indirect damages excluded.",
                FullText: "The Service Provider's total aggregate liability under this contract shall not exceed an amount equal to three (3) months of the Customer's subscription fees. " +
                         "The Service Provider is not liable for indirect, incidental, consequential, or punitive damages including but not limited to lost profits, business interruption, or data loss. " +
                         "Customer is responsible for maintaining adequate data backups. " +
                         "Force majeure events (natural disasters, acts of war, government restrictions) excuse performance and do not trigger liability.",
                Type: ClauseType.Liability),

            new ContractClause(
                ClauseNumber: 7,
                Title: "Data Privacy (GDPR Compliance)",
                Summary: "Customer data processed per GDPR; 30-day retention after termination; data portability available.",
                FullText: "Customer data is collected and processed in accordance with EU General Data Protection Regulation (GDPR). " +
                         "Personal data includes: contact information, payment details, service usage logs. " +
                         "Data retention: Active contract duration plus 30 days post-termination for billing reconciliation, then securely deleted. " +
                         "Customer rights: access, rectification, erasure ('right to be forgotten'), data portability. " +
                         "Third-party data sharing limited to payment processors and legal obligations. " +
                         "Data breach notifications provided within 72 hours as required by law. " +
                         "Data processing addendum available upon request.",
                Type: ClauseType.Privacy)
        ],
        Services:
        [
            new ActiveService("SRV-001",
                "Premium Internet Access",
                "100Mbps fiber connection with 250GB monthly data - Required base service",
                90.00m,
                false),

            new ActiveService("SRV-002",
                "Static IP Address",
                "Dedicated static IPv4 address for hosting and remote access",
                5.00m,
                true),

            new ActiveService("SRV-003",
                "Business Router Rental",
                "Enterprise-grade dual-band WiFi 6 router with advanced security",
                10.00m,
                true),

            new ActiveService("SRV-004",
                "Priority Support Package",
                "24/7 dedicated support line with 2-hour response time SLA",
                25.00m,
                true),

            new ActiveService("SRV-005",
                "Cloud Backup Service",
                "50GB encrypted cloud storage for automatic data backup",
                20.00m,
                true)
        ],
        Termination: new TerminationPolicy(
            NoticePeriodDays: 30,
            EarlyTerminationFee: 300.00m,
            TerminationSteps:
            [
                "Submit termination request via customer portal or registered mail",
                "Receive termination confirmation email with reference number",
                "Settle all outstanding invoices within 15 days",
                "Return rented equipment (router) within 30 days of termination",
                "Receive final bill with any pro-rata charges or refunds",
                "Service disconnection occurs on the termination effective date"
            ],
            RequiredDocuments:
            [
                "Government-issued ID (copy)",
                "Contract reference number (CTR-2025-P994E)",
                "Written termination request signed by account holder",
                "Proof of address if relocating (optional, for fee waiver)",
                "Equipment return receipt (for rented devices)"
            ],
            RefundPolicy: "Pro-rata refund for unused service period (minimum 7 days). Processing time: 30 business days."));

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Retrieves comprehensive contract details as structured JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <returns>JSON object with complete contract overview</returns>
    public async Task<string> GetContractDetails(string userId)
    {
        await Task.Delay(50);

        int remainingDays = (_mockContract.EndDate - DateTime.UtcNow).Days;

        var result = new
        {
            contractId = _mockContract.ContractId,
            userId = _mockContract.UserId,
            status = new
            {
                value = _mockContract.Status.ToString(),
                icon = _mockContract.Status switch
                {
                    ContractStatus.Active => "✅",
                    ContractStatus.PendingRenewal => "🔄",
                    ContractStatus.Expired => "⏰",
                    ContractStatus.Terminated => "❌",
                    ContractStatus.Suspended => "⏸️",
                    _ => "📋"
                }
            },
            plan = new
            {
                name = _mockContract.Plan.Name,
                speed = _mockContract.Plan.Speed,
                dataLimit = _mockContract.Plan.DataLimit,
                serviceLevel = _mockContract.Plan.ServiceLevel,
                includedFeatures = _mockContract.Plan.IncludedFeatures
            },
            contractPeriod = new
            {
                startDate = _mockContract.StartDate.ToString("dd/MM/yyyy"),
                endDate = _mockContract.EndDate.ToString("dd/MM/yyyy"),
                remainingDays = remainingDays > 0 ? remainingDays : 0,
                remainingMonths = remainingDays > 0 ? remainingDays / 30 : 0
            },
            billing = new
            {
                monthlyFee = _mockContract.MonthlyFee,
                billingCycle = _mockContract.BillingCycle.ToString()
            },
            services = _mockContract.Services.Select(s => new
            {
                serviceId = s.ServiceId,
                name = s.Name,
                description = s.Description,
                monthlyCost = s.MonthlyCost,
                isOptional = s.IsOptional,
                category = s.IsOptional ? "Optional" : "Required"
            }).ToList(),
            termination = new
            {
                noticePeriodDays = _mockContract.Termination.NoticePeriodDays,
                earlyTerminationFee = _mockContract.Termination.EarlyTerminationFee,
                autoRenewal = new
                {
                    enabled = true,
                    noticeDays = 60,
                    renewalDate = _mockContract.EndDate.ToString("dd/MM/yyyy")
                }
            },
            availableClauses = _mockContract.Clauses.Select(c => new
            {
                clauseNumber = c.ClauseNumber,
                title = c.Title,
                type = c.Type.ToString()
            }).ToList()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }

    /// <summary>
    /// Retrieves detailed information about a specific contract clause as JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="clauseNumber">Clause number to retrieve (1-7)</param>
    /// <returns>JSON object with complete clause details</returns>
    public async Task<string> GetContractClause(string userId, int clauseNumber)
    {
        await Task.Delay(50);

        ContractClause? clause = _mockContract.Clauses.FirstOrDefault(c => c.ClauseNumber == clauseNumber);

        if (clause == null)
        {
            var error = new
            {
                error = "Clause not found",
                requestedClauseNumber = clauseNumber,
                availableClauses = _mockContract.Clauses.Select(c => new
                {
                    clauseNumber = c.ClauseNumber,
                    title = c.Title
                }).ToList()
            };
            return JsonSerializer.Serialize(error, new JsonSerializerOptions 
            { 
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
        }

        var result = new
        {
            clauseNumber = clause.ClauseNumber,
            title = clause.Title,
            type = clause.Type.ToString(),
            summary = clause.Summary,
            fullText = clause.FullText,
            relatedInfo = clause.Type switch
            {
                ClauseType.Termination => "For termination procedures, use GetTerminationProcedure tool",
                ClauseType.DataUsage => "For current data usage, ask about billing details",
                _ => null
            }
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }

    /// <summary>
    /// Provides step-by-step termination procedure as structured JSON.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="reason">Optional termination reason for internal tracking</param>
    /// <returns>JSON object with complete termination guide</returns>
    public async Task<string> GetTerminationProcedure(string userId, string? reason = null)
    {
        await Task.Delay(50);

        DateTime earliestTerminationDate = DateTime.UtcNow.AddDays(_mockContract.Termination.NoticePeriodDays);
        bool earlyTermination = earliestTerminationDate < _mockContract.EndDate;

        var result = new
        {
            contractId = _mockContract.ContractId,
            reason = reason ?? "Not specified",
            noticePeriod = new
            {
                requiredDays = _mockContract.Termination.NoticePeriodDays,
                earliestEffectiveDate = earliestTerminationDate.ToString("dd/MM/yyyy")
            },
            fees = new
            {
                earlyTermination = new
                {
                    applicable = earlyTermination,
                    amount = earlyTermination ? _mockContract.Termination.EarlyTerminationFee : 0m,
                    reason = earlyTermination 
                        ? $"Contract ends {_mockContract.EndDate:dd/MM/yyyy}, termination before this date incurs fee"
                        : "No early termination fee (contract expired or within normal period)"
                },
                waiverEligibility = new
                {
                    available = true,
                    conditions = new[]
                    {
                        "Relocation outside service area (proof required)",
                        "Documented service failures exceeding SLA for 3 consecutive months"
                    }
                }
            },
            procedure = new
            {
                steps = _mockContract.Termination.TerminationSteps.Select((step, index) => new
                {
                    stepNumber = index + 1,
                    description = step
                }).ToList(),
                requiredDocuments = _mockContract.Termination.RequiredDocuments
            },
            refundPolicy = _mockContract.Termination.RefundPolicy,
            importantNotes = new[]
            {
                "Termination request must be submitted in writing",
                "All outstanding invoices must be settled before termination",
                "Rented equipment must be returned to avoid replacement charges",
                "Service disconnection occurs on the effective termination date"
            }
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
    }
}
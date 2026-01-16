using Microsoft.Extensions.Logging;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Attributes;
using Morgana.Framework.Providers;
using System.Text;

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
        Func<MorganaContextProvider> getContextProvider) : base(toolLogger, getContextProvider) { }

    // =========================================================================
    // DOMAIN MODELS
    // =========================================================================

    /// <summary>
    /// Represents a telecom service contract with all terms and conditions.
    /// </summary>
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

    /// <summary>
    /// Service plan tier with bandwidth and data limits.
    /// </summary>
    public record ServicePlan(
        string Name,
        string Speed,
        string DataLimit,
        string ServiceLevel,
        List<string> IncludedFeatures);

    /// <summary>
    /// Contract status indicator.
    /// </summary>
    public enum ContractStatus
    {
        Active,
        PendingRenewal,
        Expired,
        Terminated,
        Suspended
    }

    /// <summary>
    /// Billing cycle configuration.
    /// </summary>
    public enum BillingCycle
    {
        Monthly,
        Quarterly,
        Annual
    }

    /// <summary>
    /// Individual contract clause or term.
    /// </summary>
    public record ContractClause(
        int ClauseNumber,
        string Title,
        string Summary,
        string FullText,
        ClauseType Type);

    /// <summary>
    /// Type/category of contract clause.
    /// </summary>
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

    /// <summary>
    /// Active service included in the contract.
    /// </summary>
    public record ActiveService(
        string ServiceId,
        string Name,
        string Description,
        decimal MonthlyCost,
        bool IsOptional);

    /// <summary>
    /// Contract termination policy and procedures.
    /// </summary>
    public record TerminationPolicy(
        int NoticePeriodDays,
        decimal EarlyTerminationFee,
        List<string> TerminationSteps,
        List<string> RequiredDocuments,
        string RefundPolicy);

    // =========================================================================
    // MOCK DATA
    // =========================================================================

    private readonly Contract _mockContract = new(
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
            new ContractClause(1,
                "Service Level Agreement",
                "Provider guarantees 99.5% network uptime with compensation for outages exceeding 0.5%.",
                "The Provider commits to maintaining network availability of at least 99.5% per calendar month, measured as total minutes of service availability divided by total minutes in the month. In the event of service outages exceeding 0.5% of total time, the Customer shall be entitled to a pro-rata credit on the following month's invoice equivalent to 10% of the monthly fee for each additional 1% of downtime. Scheduled maintenance windows (maximum 4 hours per month, with 72 hours advance notice) are excluded from uptime calculations.",
                ClauseType.ServiceLevel),


            new ContractClause(2,
                "Payment Terms",
                "Monthly fees are due by the 15th of each month via direct debit or credit card.",
                "The Customer agrees to pay the Monthly Fee by the 15th day of each month for services provided in the preceding month. Payment shall be made via direct debit from the designated bank account or via credit card on file. Late payments incur a 2% monthly interest charge and may result in service suspension after 30 days of non-payment. The Provider will send invoice notices 10 days before the due date. Payment disputes must be raised within 30 days of invoice date.",
                ClauseType.Payment),


            new ContractClause(3,
                "Data Usage and Fair Use Policy",
                "Monthly data allowance is 250GB. Overage charges apply at €0.02/MB after exhaustion.",
                "The Service Plan includes a monthly data transfer allowance of 250 gigabytes (GB). Data usage is calculated as the sum of download and upload traffic. Upon exceeding the monthly allowance, overage charges of €0.02 per megabyte (MB) apply, calculated daily and billed in the following month. The Provider reserves the right to implement traffic shaping during peak hours (18:00-23:00) if usage exceeds 10GB/day consistently. Customers engaging in excessive usage (>500GB/month for three consecutive months) may be required to upgrade to an unlimited plan or face service restrictions.",
                ClauseType.DataUsage),


            new ContractClause(4,
                "Contract Termination",
                "Termination requires 30 days written notice. Early termination fee: €300.",
                "Either party may terminate this Contract by providing written notice 30 days in advance. Termination requests must be submitted via registered mail or through the customer portal with documented confirmation. Early termination (before contract end date) requires payment of an Early Termination Fee of €300, calculated as remaining months multiplied by €25 per month (minimum €300). This fee is waived in cases of: (a) service unavailability exceeding 5% in any three-month period, (b) relocation to an area without service coverage (proof required), (c) customer death or permanent disability. Upon termination, all outstanding invoices must be settled within 15 days.",
                ClauseType.Termination),


            new ContractClause(5,
                "Automatic Renewal Terms",
                "Contract auto-renews for 12 months unless terminated 60 days before expiration.",
                "Unless either party provides written notice of non-renewal at least 60 days before the Contract End Date, this Contract shall automatically renew for successive 12-month periods under the same terms and conditions, subject to standard price adjustments (maximum 5% annual increase, aligned with consumer price index). Renewal notices will be sent 90 days before expiration. Customers may opt out of automatic renewal at any time through the customer portal. Renewed contracts may include updated terms if regulatory requirements change, with 30 days advance notice of material changes.",
                ClauseType.RenewalTerms),


            new ContractClause(6,
                "Limitation of Liability",
                "Provider liability is limited to 3 months of monthly fees for service failures.",
                "The Provider's total liability for any claims arising from service failures, data loss, or security breaches is limited to an amount equal to three (3) months of the Customer's Monthly Fee. The Provider shall not be liable for indirect, consequential, or punitive damages including but not limited to loss of profits, business interruption, or data loss. This limitation does not apply to: (a) gross negligence or willful misconduct, (b) personal injury or death, (c) breaches of data protection regulations. The Customer acknowledges that internet connectivity depends on third-party infrastructure and accepts associated risks.",
                ClauseType.Liability),


            new ContractClause(7,
                "Data Privacy and Security",
                "Customer data is processed according to GDPR with encryption and access controls.",
                "The Provider processes Customer data in accordance with EU General Data Protection Regulation (GDPR) and applicable national laws. Personal data collected includes: contact information, payment details, usage statistics, and support communications. Data is stored on EU-based servers with AES-256 encryption at rest and TLS 1.3 in transit. Access is restricted to authorized personnel only. Customers have rights to: access their data, request corrections, demand deletion, and export data in machine-readable format. Data retention: active contracts plus 7 years for tax purposes. Third-party data sharing limited to: payment processors, service infrastructure partners (anonymized), and legal compliance (when required). Data breach notifications within 72 hours.",
                ClauseType.Privacy)
        ],
        Services:
        [
            new ActiveService("SRV-001",
                "Premium Internet Access",
                "100Mbps fiber connection with 250GB monthly data",
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
    /// Retrieves comprehensive contract details including plan, services, and key terms.
    /// Provides high-level overview suitable for general inquiries.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <returns>Formatted contract overview with essential information</returns>
    public async Task<string> GetContractDetails(string userId)
    {
        await Task.Delay(150);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📜 Contract Overview: {_mockContract.ContractId}");
        sb.AppendLine();

        // Status with icon
        string statusIcon = _mockContract.Status switch
        {
            ContractStatus.Active => "✅",
            ContractStatus.PendingRenewal => "🔄",
            ContractStatus.Expired => "⏰",
            ContractStatus.Terminated => "❌",
            ContractStatus.Suspended => "⏸️",
            _ => "📋"
        };

        sb.AppendLine($"Status: {statusIcon} {_mockContract.Status}");
        sb.AppendLine($"Service Plan: {_mockContract.Plan.Name}");
        sb.AppendLine($"Contract Period: {_mockContract.StartDate:dd/MM/yyyy} to {_mockContract.EndDate:dd/MM/yyyy}");

        int remainingDays = (_mockContract.EndDate - DateTime.UtcNow).Days;
        if (remainingDays > 0)
        {
            sb.AppendLine($"Time Remaining: {remainingDays} days ({remainingDays / 30} months)");
        }

        sb.AppendLine($"Monthly Fee: €{_mockContract.MonthlyFee:F2} ({_mockContract.BillingCycle})");
        sb.AppendLine();

        // Service Plan Details
        sb.AppendLine("📡 Service Plan Specifications:");
        sb.AppendLine($"  • Speed: {_mockContract.Plan.Speed}");
        sb.AppendLine($"  • Data Limit: {_mockContract.Plan.DataLimit}");
        sb.AppendLine($"  • SLA: {_mockContract.Plan.ServiceLevel}");
        sb.AppendLine();

        // Included Features
        sb.AppendLine("✨ Included Features:");
        foreach (string feature in _mockContract.Plan.IncludedFeatures)
        {
            sb.AppendLine($"  ✓ {feature}");
        }
        sb.AppendLine();

        // Active Services Breakdown
        sb.AppendLine("💼 Active Services:");
        foreach (ActiveService service in _mockContract.Services)
        {
            string optionalBadge = service.IsOptional ? " (Optional)" : " (Required)";
            sb.AppendLine($"  • {service.Name}{optionalBadge} - €{service.MonthlyCost:F2}/month");
            sb.AppendLine($"    {service.Description}");
        }
        sb.AppendLine();

        // Quick Termination Info
        sb.AppendLine("📋 Quick Facts:");
        sb.AppendLine($"  • Notice Period: {_mockContract.Termination.NoticePeriodDays} days");
        sb.AppendLine($"  • Early Termination Fee: €{_mockContract.Termination.EarlyTerminationFee:F2}");
        sb.AppendLine($"  • Auto-Renewal: Yes (notify 60 days before {_mockContract.EndDate:dd/MM/yyyy})");
        sb.AppendLine();

        sb.AppendLine("💡 Need More Info?");
        sb.AppendLine("  • Ask about specific contract clauses (1-7)");
        sb.AppendLine("  • Inquire about termination procedures");
        sb.AppendLine("  • Request service modification options");

        return sb.ToString();
    }

    /// <summary>
    /// Retrieves detailed information about a specific contract clause.
    /// Provides full legal text and explanations for transparency.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="clauseNumber">Clause number to retrieve (1-7)</param>
    /// <returns>Complete clause text with summary and legal language</returns>
    public async Task<string> GetContractClause(string userId, int clauseNumber)
    {
        await Task.Delay(100);

        ContractClause? clause = _mockContract.Clauses.FirstOrDefault(c => c.ClauseNumber == clauseNumber);

        if (clause == null)
        {
            return $"❌ Clause {clauseNumber} not found. Available clauses: 1-{_mockContract.Clauses.Count}. " +
                   $"Topics: {string.Join(", ", _mockContract.Clauses.Select(c => $"{c.ClauseNumber}. {c.Title}"))}";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📄 Clause {clause.ClauseNumber}: {clause.Title}");
        sb.AppendLine();
        sb.AppendLine($"Type: {clause.Type}");
        sb.AppendLine();
        sb.AppendLine("Summary:");
        sb.AppendLine($"_{clause.Summary}_");
        sb.AppendLine();
        sb.AppendLine("Full Legal Text:");
        sb.AppendLine(WrapText(clause.FullText, 80));

        switch (clause.Type)
        {
            case ClauseType.Termination:
                sb.AppendLine();
                sb.AppendLine("💡 To initiate termination, ask me about the termination procedure.");
                break;
            case ClauseType.DataUsage:
                sb.AppendLine();
                sb.AppendLine("💡 Check your current data usage by asking about billing details.");
                break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Provides step-by-step termination procedure with required documents.
    /// Guides users through the contract cancellation process.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <param name="reason">Optional reason for termination (for internal tracking)</param>
    /// <returns>Detailed termination guide with steps and requirements</returns>
    public async Task<string> GetTerminationProcedure(string userId, string? reason = null)
    {
        await Task.Delay(180);

        TerminationPolicy termination = _mockContract.Termination;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("📋 Contract Termination Procedure");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(reason))
        {
            sb.AppendLine($"Termination Reason: {reason}");
            sb.AppendLine("_(This will be recorded for internal purposes)_");
            sb.AppendLine();
        }

        // Notice Period Warning
        int remainingDays = (_mockContract.EndDate - DateTime.UtcNow).Days;
        if (remainingDays > termination.NoticePeriodDays)
        {
            decimal earlyFee = termination.EarlyTerminationFee;
            sb.AppendLine("⚠️ Early Termination Notice:");
            sb.AppendLine($"Your contract expires in {remainingDays} days ({_mockContract.EndDate:dd/MM/yyyy}).");
            sb.AppendLine($"Terminating now will incur an Early Termination Fee of €{earlyFee:F2}.");
            sb.AppendLine();
        }

        // Required Documents
        sb.AppendLine("📄 Required Documents:");
        for (int i = 0; i < termination.RequiredDocuments.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {termination.RequiredDocuments[i]}");
        }
        sb.AppendLine();

        // Step-by-Step Process
        sb.AppendLine("📝 Termination Steps:");
        for (int i = 0; i < termination.TerminationSteps.Count; i++)
        {
            sb.AppendLine($"  Step {i + 1}: {termination.TerminationSteps[i]}");
        }
        sb.AppendLine();

        // Financial Details
        sb.AppendLine("💰 Financial Details:");
        sb.AppendLine($"  • Notice Period: {termination.NoticePeriodDays} days from request submission");
        sb.AppendLine($"  • Early Termination Fee: €{termination.EarlyTerminationFee:F2} (if applicable)");
        sb.AppendLine($"  • Equipment Deposit: €0 (free rental program)");
        sb.AppendLine($"  • Refund Policy: {termination.RefundPolicy}");
        sb.AppendLine();

        // Timeline
        DateTime effectiveDate = DateTime.UtcNow.AddDays(termination.NoticePeriodDays);
        sb.AppendLine("📅 Estimated Timeline:");
        sb.AppendLine($"  • Request Submission: Today ({DateTime.UtcNow:dd/MM/yyyy})");
        sb.AppendLine($"  • Notice Period Ends: {effectiveDate:dd/MM/yyyy}");
        sb.AppendLine($"  • Service Disconnection: {effectiveDate:dd/MM/yyyy}");
        sb.AppendLine($"  • Equipment Return Due: {effectiveDate.AddDays(30):dd/MM/yyyy}");
        sb.AppendLine($"  • Final Bill Issued: {effectiveDate.AddDays(15):dd/MM/yyyy}");
        sb.AppendLine($"  • Refund Processing: Up to 30 business days after final bill");
        sb.AppendLine();

        // Fee Waiver Conditions
        sb.AppendLine("💡 Fee Waiver Eligibility:");
        sb.AppendLine("Early termination fees may be waived if:");
        sb.AppendLine("  • Service downtime exceeded 5% in any 3-month period");
        sb.AppendLine("  • You're relocating to an area without coverage (proof required)");
        sb.AppendLine("  • Account holder death or permanent disability (documentation required)");
        sb.AppendLine();

        sb.AppendLine("⚠️ Important Notes:");
        sb.AppendLine("  • All outstanding invoices must be paid before termination");
        sb.AppendLine("  • Failure to return equipment may result in €200 replacement charge");
        sb.AppendLine("  • Termination cannot be cancelled once confirmed");
        sb.AppendLine();

        return sb.ToString();
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private static string WrapText(string text, int maxLineLength)
    {
        string[] words = text.Split(' ');
        List<string> lines = [];
        StringBuilder currentLine = new StringBuilder();

        foreach (string word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxLineLength)
            {
                lines.Add(currentLine.ToString());
                currentLine.Clear();
            }

            if (currentLine.Length > 0)
                currentLine.Append(' ');

            currentLine.Append(word);
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine.ToString());

        return string.Join("\n", lines);
    }
}
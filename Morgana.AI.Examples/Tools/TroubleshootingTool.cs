using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Providers;
using System.Text;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Examples.Tools;

/// <summary>
/// Professional troubleshooting tool with diagnostic capabilities and interactive step-by-step guides.
/// Provides realistic network diagnostics and structured problem resolution workflows.
/// </summary>
[ProvidesToolForIntent("troubleshooting")]
public class TroubleshootingTool : MorganaTool
{
    public TroubleshootingTool(
        ILogger toolLogger,
        Func<MorganaContextProvider> getContextProvider) : base(toolLogger, getContextProvider) { }

    // =========================================================================
    // DOMAIN MODELS
    // =========================================================================

    /// <summary>
    /// Network diagnostic results with detailed metrics and status indicators.
    /// </summary>
    public record DiagnosticResult(
        DateTime Timestamp,
        ConnectionStatus OverallStatus,
        ModemStatus Modem,
        NetworkMetrics Metrics,
        List<DiagnosticIssue> DetectedIssues,
        List<string> Recommendations);

    /// <summary>
    /// Overall connection health status.
    /// </summary>
    public enum ConnectionStatus
    {
        Excellent,
        Good,
        Fair,
        Poor,
        Critical,
        Offline
    }

    /// <summary>
    /// Modem/router status information.
    /// </summary>
    public record ModemStatus(
        bool IsOnline,
        string FirmwareVersion,
        int UptimeHours,
        string SignalStrength,
        int ConnectedDevices,
        double CpuUsagePercent,
        double MemoryUsagePercent);

    /// <summary>
    /// Network performance metrics.
    /// </summary>
    public record NetworkMetrics(
        double DownloadSpeedMbps,
        double UploadSpeedMbps,
        int LatencyMs,
        double PacketLossPercent,
        double JitterMs,
        string DnsResponseTime);

    /// <summary>
    /// Identified diagnostic issue with severity.
    /// </summary>
    public record DiagnosticIssue(
        string Issue,
        IssueSeverity Severity,
        string Description,
        List<string> SuggestedActions);

    /// <summary>
    /// Issue severity classification.
    /// </summary>
    public enum IssueSeverity
    {
        Info,
        Warning,
        Critical
    }

    /// <summary>
    /// Interactive troubleshooting guide with step-by-step instructions.
    /// </summary>
    public record TroubleshootingGuide(
        string GuideId,
        string ProblemType,
        string Description,
        EstimatedDuration Duration,
        DifficultyLevel Difficulty,
        List<TroubleshootingStep> Steps,
        List<string> RequiredTools,
        string SuccessCriteria);

    /// <summary>
    /// Estimated time to complete guide.
    /// </summary>
    public enum EstimatedDuration
    {
        FiveMinutes,
        TenMinutes,
        FifteenMinutes,
        ThirtyMinutes
    }

    /// <summary>
    /// Technical difficulty level.
    /// </summary>
    public enum DifficultyLevel
    {
        Beginner,
        Intermediate,
        Advanced
    }

    /// <summary>
    /// Individual troubleshooting step with verification.
    /// </summary>
    public record TroubleshootingStep(
        int StepNumber,
        string Title,
        string Instructions,
        List<string> DetailedActions,
        string ExpectedOutcome,
        string IfSuccessful,
        string IfUnsuccessful);

    // =========================================================================
    // MOCK DATA - TROUBLESHOOTING GUIDES
    // =========================================================================

    private readonly Dictionary<string, TroubleshootingGuide> _guides = new()
    {
        ["no-internet"] = new TroubleshootingGuide(
            GuideId: "TSG-001",
            ProblemType: "No Internet Connection",
            Description: "Complete absence of internet connectivity. Device shows connected to WiFi but no internet access.",
            Duration: EstimatedDuration.FifteenMinutes,
            Difficulty: DifficultyLevel.Beginner,
            Steps:
            [
                new TroubleshootingStep(1,
                    "Verify Modem Power and Lights",
                    "Check if your modem/router is properly powered and displaying the correct status lights.",
                    [
                        "Locate your modem/router (usually near where cable/fiber enters your home)",
                        "Check if the device is plugged into power outlet",
                        "Look for these indicator lights: POWER (solid green), DSL/WAN (solid green), INTERNET (solid green)",
                        "If any lights are red, amber, or off, note which ones"
                    ],
                    "All essential lights should be solid green, indicating active connection.",
                    "Power is confirmed good. Proceed to Step 2.",
                    "If power light is off, check power cable and outlet. If WAN/DSL light is off, check cable connections."),


                new TroubleshootingStep(2,
                    "Check Cable Connections",
                    "Ensure all physical cables are securely connected to prevent signal loss.",
                    [
                        "Unplug the cable from WAN/DSL port on modem",
                        "Inspect both ends for damage (bent pins, corrosion, cracks)",
                        "Firmly reconnect the cable to WAN/DSL port until you hear a click",
                        "Check ethernet cable from modem to your computer (if wired)",
                        "Ensure no cables are pinched, bent sharply, or damaged"
                    ],
                    "Cables should be firmly seated with no visible damage.",
                    "Cables are secure. Wait 2 minutes and proceed to Step 3.",
                    "If cables are damaged, you may need a replacement. Contact support for cable delivery."),


                new TroubleshootingStep(3,
                    "Power Cycle the Modem",
                    "Restart the modem to clear temporary glitches and re-establish connection.",
                    [
                        "Unplug the power cable from the modem (not from the wall outlet)",
                        "Wait exactly 30 seconds (count to 30 slowly)",
                        "While waiting, also turn off your computer/device",
                        "Plug the modem power cable back in",
                        "Wait 2-3 minutes for modem to fully boot (lights will flash then stabilize)",
                        "Turn your computer/device back on",
                        "Wait 1 minute for connection to establish"
                    ],
                    "Modem lights should stabilize to solid green within 3 minutes.",
                    "Connection restored! Test by opening a website. Problem solved.",
                    "If still no connection after power cycle, proceed to Step 4."),


                new TroubleshootingStep(4,
                    "Test with Different Device",
                    "Determine if the issue is with your device or the internet connection itself.",
                    [
                        "Try connecting a different device (smartphone, tablet, another computer)",
                        "Use the same connection method (WiFi or Ethernet)",
                        "Attempt to browse a website or use an app requiring internet",
                        "If using WiFi, ensure device is connected to correct network"
                    ],
                    "Different device should either connect successfully or fail similarly.",
                    "If other device works, issue is with your original device. Check device WiFi settings.",
                    "If no devices can connect, the issue is with your internet service. Proceed to Step 5."),


                new TroubleshootingStep(5,
                    "Check Service Status and Contact Support",
                    "Verify if there's a known outage and escalate to technical support if needed.",
                    [
                        "Ask me: 'Check service status for my area'",
                        "I'll verify if there are any reported outages affecting your service",
                        "If no outage, I'll create a support ticket for you",
                        "Technical support will contact you within 2 hours",
                        "Keep your modem powered on for remote diagnostics"
                    ],
                    "You'll receive confirmation of outage status or support ticket number.",
                    "Support ticket created. You'll be contacted shortly.",
                    "If outage detected, estimated restoration time will be provided.")
            ],
            RequiredTools: ["No tools required", "Access to modem/router"],
            SuccessCriteria: "You can successfully browse websites and internet-dependent applications work normally."),

        ["slow-connection"] = new TroubleshootingGuide(
            GuideId: "TSG-002",
            ProblemType: "Slow Internet Speed",
            Description: "Internet works but significantly slower than expected or advertised speed.",
            Duration: EstimatedDuration.TenMinutes,
            Difficulty: DifficultyLevel.Beginner,
            Steps:
            [
                new TroubleshootingStep(1,
                    "Run Speed Test",
                    "Establish baseline measurements to quantify the speed issue.",
                    [
                        "Close all applications except web browser",
                        "Disconnect all other devices from network (phones, tablets, smart TVs)",
                        "Ask me: 'Run speed test'",
                        "I'll perform an automated speed test and show results",
                        "Note the download speed, upload speed, and latency values"
                    ],
                    "Speed test results should be within 20% of your plan's advertised speed.",
                    "If speeds are normal (>80Mbps), issue may be device-specific. Check Task Manager for bandwidth hogs.",
                    "If speeds are significantly low (<50Mbps), continue troubleshooting."),


                new TroubleshootingStep(2,
                    "Check Connected Devices",
                    "Identify if other devices are consuming bandwidth and causing congestion.",
                    [
                        "Ask me: 'Show connected devices'",
                        "I'll list all devices currently using your network",
                        "Look for unexpected devices or many simultaneous connections",
                        "Devices with high activity: streaming (Netflix, YouTube), downloads, video calls",
                        "Temporarily disconnect non-essential devices from WiFi"
                    ],
                    "Typical household: 5-10 devices. More than 15 may indicate unauthorized access.",
                    "If many devices disconnected and speed improved, bandwidth congestion was the cause.",
                    "If speed remains slow with minimal devices, proceed to Step 3."),


                new TroubleshootingStep(3,
                    "Optimize WiFi Connection",
                    "Improve wireless signal strength by adjusting device position or switching bands.",
                    [
                        "Move closer to the router (within 10 meters, line of sight if possible)",
                        "Remove obstacles between device and router (walls, metal objects, appliances)",
                        "If your device supports 5GHz WiFi, switch to it (less congestion, faster speeds)",
                        "Change router WiFi channel if many neighbors' networks overlap (check in router settings)",
                        "Avoid placing router near: microwaves, cordless phones, baby monitors, Bluetooth devices"
                    ],
                    "Signal strength indicator should show 3-4 bars. Speed should improve by 20-50%.",
                    "Speed improved significantly! Optimize router placement permanently.",
                    "If WiFi optimization didn't help, try wired connection next (Step 4)."),


                new TroubleshootingStep(4,
                    "Test with Wired Connection",
                    "Eliminate WiFi as a variable by testing with direct Ethernet connection.",
                    [
                        "Connect an Ethernet cable from modem to your computer's network port",
                        "Disable WiFi on your computer temporarily",
                        "Wait 30 seconds for wired connection to establish",
                        "Run speed test again (ask me: 'Run speed test')",
                        "Compare results to previous WiFi test"
                    ],
                    "Wired connection should be faster and more stable than WiFi.",
                    "If wired is fast but WiFi is slow, issue is with WiFi coverage. Consider WiFi extender.",
                    "If wired is also slow, issue is with internet service itself. Proceed to Step 5."),


                new TroubleshootingStep(5,
                    "Contact Advanced Support",
                    "Escalate to technical team for line quality testing and potential service issues.",
                    [
                        "Ask me: 'Create support ticket for slow speeds'",
                        "Provide speed test results from Step 1 and Step 4",
                        "Technical team will run remote line diagnostics",
                        "They may schedule on-site visit if hardware issues suspected",
                        "Average resolution time: 24-48 hours"
                    ],
                    "Support ticket created with all diagnostic data attached.",
                    "You'll receive contact from support team within 4 hours.",
                    "If urgent, you can call priority support line at 1-800-MORGANA.")
            ],
            RequiredTools: ["Ethernet cable (for Step 4)", "Access to router settings (optional)"],
            SuccessCriteria: "Download speeds consistently achieve 80% or more of your plan's advertised rate (e.g., 80Mbps on a 100Mbps plan)."),

        ["wifi-issues"] = new TroubleshootingGuide(
            GuideId: "TSG-003",
            ProblemType: "WiFi Connectivity Problems",
            Description: "Difficulty connecting to WiFi, frequent disconnections, or devices can't find the network.",
            Duration: EstimatedDuration.FifteenMinutes,
            Difficulty: DifficultyLevel.Intermediate,
            Steps:
            [
                new TroubleshootingStep(1,
                    "Verify WiFi Network Visibility",
                    "Confirm your WiFi network is broadcasting and detectable by devices.",
                    [
                        "Open WiFi settings on your device (phone, laptop, tablet)",
                        "Look for your network name (SSID) in the available networks list",
                        "Check if network shows security type (WPA2/WPA3)",
                        "Note signal strength bars (even if connection fails)",
                        "If network not visible, try refreshing/rescanning WiFi networks"
                    ],
                    "Your network should appear in available networks with 1-4 signal bars.",
                    "Network is visible. Password authentication issue likely (proceed to Step 2).",
                    "Network not visible. Router WiFi may be disabled. Check router settings or restart router."),


                new TroubleshootingStep(2,
                    "Verify WiFi Password",
                    "Ensure correct password is being used and there are no input errors.",
                    [
                        "Ask me: 'What is my WiFi password?'",
                        "I'll securely retrieve your WiFi password",
                        "Carefully re-enter password (passwords are case-sensitive)",
                        "Common mistakes: confusing O (letter) with 0 (zero), I (letter) with 1 (number)",
                        "If unsure, use 'Show password' checkbox while typing",
                        "For WPA3 networks, ensure device supports WPA3 (2018 or newer devices)"
                    ],
                    "Connection should succeed immediately after entering correct password.",
                    "Connected successfully! If problem recurs, proceed to Step 3 for stability improvements.",
                    "Still can't connect with correct password? Proceed to Step 3 for network reset."),


                new TroubleshootingStep(3,
                    "Forget and Reconnect Network",
                    "Clear saved network data and re-establish connection from scratch.",
                    [
                        "Open WiFi settings and find your network in saved/known networks",
                        "Select 'Forget This Network' or 'Remove Network'",
                        "Restart your device (fully power off, then on)",
                        "Open WiFi settings again and scan for networks",
                        "Select your network and enter password as if connecting for first time",
                        "Enable 'Connect Automatically' option"
                    ],
                    "Device should connect and remain connected without interruptions.",
                    "Connection stable! Problem was corrupted network profile.",
                    "Still experiencing issues after network reset? Proceed to Step 4."),


                new TroubleshootingStep(4,
                    "Change Router WiFi Channel",
                    "Reduce interference from neighboring networks by switching to less congested channel.",
                    [
                        "Ask me: 'Scan WiFi channels for interference'",
                        "I'll analyze nearby networks and recommend optimal channel",
                        "Log into router admin panel (ask me for router login details if needed)",
                        "Navigate to Wireless Settings → WiFi Channel",
                        "For 2.4GHz: Select channel 1, 6, or 11 (non-overlapping)",
                        "For 5GHz: Select any channel (all are non-overlapping)",
                        "Apply settings and wait for router to restart (2-3 minutes)"
                    ],
                    "Connection stability and speed should improve significantly.",
                    "Channel changed successfully. Reconnect devices and monitor stability.",
                    "If channel change didn't help, WiFi hardware issue possible (Step 5)."),


                new TroubleshootingStep(5,
                    "Update Router Firmware and Factory Reset",
                    "Resolve software bugs or corrupted settings via firmware update or reset.",
                    [
                        "Ask me: 'Check for router firmware updates'",
                        "If update available, I'll guide you through installation",
                        "IMPORTANT: Do not power off router during firmware update (5-10 minutes)",
                        "If no updates or problem persists, consider factory reset:",
                        "  • Locate reset button on router (small hole, requires pin/paperclip)",
                        "  • Press and hold for 10 seconds while router is powered on",
                        "  • Router will restart with default settings",
                        "  • Reconfigure WiFi name and password (ask me for help)",
                        "CAUTION: Factory reset erases all custom settings"
                    ],
                    "Router should boot up with stable WiFi after firmware update or reset.",
                    "Firmware updated/reset successful. Reconfigure settings and test connection.",
                    "If problems persist after firmware update and reset, hardware replacement needed. Contact support.")
            ],
            RequiredTools:
            [
                "Pin or paperclip (for factory reset)",
                "Router admin credentials (ask me if you don't have them)",
                "Computer or phone to access router settings"
            ],
            SuccessCriteria: "Devices connect to WiFi successfully within 5 seconds and remain connected without drops for at least 1 hour.")
    };

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Runs comprehensive network diagnostics on user's connection.
    /// Provides detailed metrics, status indicators, and issue identification.
    /// </summary>
    /// <param name="userId">User identifier (retrieved from context)</param>
    /// <returns>Formatted diagnostic report with metrics and recommendations</returns>
    public async Task<string> RunDiagnostics(string userId)
    {
        await Task.Delay(500); // Simulate diagnostic tools running

        // Generate realistic diagnostic results
        DiagnosticResult result = new DiagnosticResult(
            Timestamp: DateTime.Now,
            OverallStatus: ConnectionStatus.Good,
            Modem: new ModemStatus(
                IsOnline: true,
                FirmwareVersion: "v2.4.8",
                UptimeHours: 168,
                SignalStrength: "87% (-45 dBm)",
                ConnectedDevices: 8,
                CpuUsagePercent: 34.2,
                MemoryUsagePercent: 62.8),
            Metrics: new NetworkMetrics(
                DownloadSpeedMbps: 98.3,
                UploadSpeedMbps: 19.7,
                LatencyMs: 12,
                PacketLossPercent: 0.2,
                JitterMs: 2.1,
                DnsResponseTime: "18ms"),
            DetectedIssues:
            [
                new DiagnosticIssue("High Number of Connected Devices",
                    IssueSeverity.Warning,
                    "8 devices currently connected to network. May cause congestion during peak usage.",
                    [
                        "Review connected devices and disconnect unused ones",
                        "Consider upgrading to higher bandwidth plan if all devices are needed",
                        "Enable Quality of Service (QoS) prioritization in router"
                    ]),

                new DiagnosticIssue("Router Firmware Slightly Outdated",
                    IssueSeverity.Info,
                    "Firmware v2.4.8 detected. Newer version v2.5.1 available with performance improvements.",
                    [
                        "Ask me: 'Update router firmware'",
                        "Update during off-peak hours (takes 5-10 minutes)",
                        "Backup router settings before update"
                    ])
            ],
            Recommendations:
            [
                "Connection quality is good. No immediate action required.",
                "Minor latency fluctuation detected. Consider restarting router weekly.",
                "Router has been running for 7 days. Monthly restart recommended for optimal performance.",
                "Speed test shows 98% of plan capacity - excellent performance."
            ]);

        // Format output
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("🔧 **Network Diagnostic Report**");
        sb.AppendLine($"**Scan Time:** {result.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Overall Status with emoji
        string statusIcon = result.OverallStatus switch
        {
            ConnectionStatus.Excellent => "🟢",
            ConnectionStatus.Good => "🟢",
            ConnectionStatus.Fair => "🟡",
            ConnectionStatus.Poor => "🟠",
            ConnectionStatus.Critical => "🔴",
            ConnectionStatus.Offline => "⚫",
            _ => "⚪"
        };
        sb.AppendLine($"**Overall Status:** {statusIcon} {result.OverallStatus}");
        sb.AppendLine();

        // Modem Status
        sb.AppendLine("**📡 Modem/Router Status:**");
        sb.AppendLine($"  • Online: {(result.Modem.IsOnline ? "✅ Yes" : "❌ No")}");
        sb.AppendLine($"  • Firmware: {result.Modem.FirmwareVersion}");
        sb.AppendLine($"  • Uptime: {result.Modem.UptimeHours} hours ({result.Modem.UptimeHours / 24} days)");
        sb.AppendLine($"  • Signal Strength: {result.Modem.SignalStrength}");
        sb.AppendLine($"  • Connected Devices: {result.Modem.ConnectedDevices}");
        sb.AppendLine($"  • CPU Usage: {result.Modem.CpuUsagePercent:F1}%");
        sb.AppendLine($"  • Memory Usage: {result.Modem.MemoryUsagePercent:F1}%");
        sb.AppendLine();

        // Network Metrics
        sb.AppendLine("**📊 Network Performance Metrics:**");
        sb.AppendLine($"  • Download Speed: {result.Metrics.DownloadSpeedMbps:F1} Mbps {GetSpeedRating(result.Metrics.DownloadSpeedMbps, 100)}");
        sb.AppendLine($"  • Upload Speed: {result.Metrics.UploadSpeedMbps:F1} Mbps {GetSpeedRating(result.Metrics.UploadSpeedMbps, 20)}");
        sb.AppendLine($"  • Latency (Ping): {result.Metrics.LatencyMs} ms {GetLatencyRating(result.Metrics.LatencyMs)}");
        sb.AppendLine($"  • Packet Loss: {result.Metrics.PacketLossPercent:F2}% {GetPacketLossRating(result.Metrics.PacketLossPercent)}");
        sb.AppendLine($"  • Jitter: {result.Metrics.JitterMs:F1} ms {GetJitterRating(result.Metrics.JitterMs)}");
        sb.AppendLine($"  • DNS Response: {result.Metrics.DnsResponseTime}");
        sb.AppendLine();

        // Detected Issues
        if (result.DetectedIssues.Any())
        {
            sb.AppendLine("**⚠️ Detected Issues:**");
            foreach (DiagnosticIssue issue in result.DetectedIssues)
            {
                string severityIcon = issue.Severity switch
                {
                    IssueSeverity.Critical => "🔴",
                    IssueSeverity.Warning => "🟡",
                    IssueSeverity.Info => "ℹ️",
                    _ => "•"
                };
                sb.AppendLine($"{severityIcon} **{issue.Issue}** ({issue.Severity})");
                sb.AppendLine($"   {issue.Description}");
                sb.AppendLine("   Suggested actions:");
                foreach (string action in issue.SuggestedActions)
                {
                    sb.AppendLine($"     • {action}");
                }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("**✅ No Issues Detected**");
            sb.AppendLine("Your network is operating optimally.");
            sb.AppendLine();
        }

        // Recommendations
        sb.AppendLine("**💡 Recommendations:**");
        foreach (string recommendation in result.Recommendations)
        {
            sb.AppendLine($"  • {recommendation}");
        }
        sb.AppendLine();

        sb.AppendLine("**Need Help?** Ask me about:");
        sb.AppendLine("  • 'Show troubleshooting guides' - Interactive step-by-step help");
        sb.AppendLine("  • 'List connected devices' - See what's using your network");
        sb.AppendLine("  • 'Run speed test' - Quick performance check");

        return sb.ToString();
    }

    /// <summary>
    /// Retrieves an interactive troubleshooting guide for a specific problem type.
    /// Provides step-by-step instructions with detailed actions and expected outcomes.
    /// </summary>
    /// <param name="issueType">Problem type identifier: "no-internet", "slow-connection", "wifi-issues"</param>
    /// <returns>Complete troubleshooting guide with interactive steps</returns>
    public async Task<string> GetTroubleshootingGuide(string issueType)
    {
        await Task.Delay(150);

        if (!_guides.TryGetValue(issueType, out TroubleshootingGuide? guide))
        {
            return $"❌ Troubleshooting guide '{issueType}' not found.\n\n" +
                   $"**Available Guides:**\n" +
                   $"  • `no-internet` - Complete loss of connectivity\n" +
                   $"  • `slow-connection` - Speeds slower than expected\n" +
                   $"  • `wifi-issues` - WiFi connection or stability problems\n\n" +
                   $"Ask me: 'Show [guide name] troubleshooting guide'";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"📖 **Troubleshooting Guide: {guide.ProblemType}**");
        sb.AppendLine();
        sb.AppendLine($"**Guide ID:** {guide.GuideId}");
        sb.AppendLine($"**Description:** {guide.Description}");
        sb.AppendLine($"**Estimated Time:** {FormatDuration(guide.Duration)}");
        sb.AppendLine($"**Difficulty:** {guide.Difficulty}");
        sb.AppendLine();

        // Required Tools
        if (guide.RequiredTools.Any())
        {
            sb.AppendLine("**🛠️ Required Tools/Access:**");
            foreach (string tool in guide.RequiredTools)
            {
                sb.AppendLine($"  • {tool}");
            }
            sb.AppendLine();
        }

        // Success Criteria
        sb.AppendLine("**✅ Success Criteria:**");
        sb.AppendLine($"{guide.SuccessCriteria}");
        sb.AppendLine();

        // Steps Overview
        sb.AppendLine($"**📋 Steps Overview ({guide.Steps.Count} steps):**");
        for (int i = 0; i < guide.Steps.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {guide.Steps[i].Title}");
        }
        sb.AppendLine();

        // Detailed Steps
        sb.AppendLine("**📝 Detailed Instructions:**");
        sb.AppendLine();
        
        foreach (TroubleshootingStep step in guide.Steps)
        {
            sb.AppendLine($"**═══ Step {step.StepNumber}: {step.Title} ═══**");
            sb.AppendLine();
            sb.AppendLine($"_{step.Instructions}_");
            sb.AppendLine();
            sb.AppendLine("**Actions:**");
            foreach (string action in step.DetailedActions)
            {
                sb.AppendLine($"  ✓ {action}");
            }
            sb.AppendLine();
            sb.AppendLine($"**Expected Outcome:** {step.ExpectedOutcome}");
            sb.AppendLine();
            sb.AppendLine($"✅ **If Successful:** {step.IfSuccessful}");
            sb.AppendLine($"❌ **If Unsuccessful:** {step.IfUnsuccessful}");
            sb.AppendLine();
            sb.AppendLine(new string('─', 80));
            sb.AppendLine();
        }

        // Interactive Guidance Offer
        sb.AppendLine("**💬 Interactive Assistance Available:**");
        sb.AppendLine("I can guide you through these steps one at a time!");
        sb.AppendLine("Just say: 'Guide me through step 1' and I'll provide real-time assistance.");
        sb.AppendLine();
        sb.AppendLine("**Quick Actions:**");
        sb.AppendLine("  • 'Run diagnostics' - Get current network status before starting");
        sb.AppendLine("  • 'List other guides' - See all available troubleshooting guides");
        sb.AppendLine("  • 'Create support ticket' - Escalate to technical team");

        return sb.ToString();
    }

    /// <summary>
    /// Lists all available troubleshooting guides with brief descriptions.
    /// Helps users discover appropriate guides for their problem.
    /// Sets quick replies for direct guide selection.
    /// </summary>
    /// <returns>Catalog of all troubleshooting resources</returns>
    public async Task<string> ListTroubleshootingGuides()
    {
        await Task.Delay(80);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("📚 **Available Troubleshooting Guides**");
        sb.AppendLine();

        foreach ((string key, TroubleshootingGuide guide) in _guides.OrderBy(g => g.Key))
        {
            string difficultyIcon = guide.Difficulty switch
            {
                DifficultyLevel.Beginner => "🟢",
                DifficultyLevel.Intermediate => "🟡",
                DifficultyLevel.Advanced => "🔴",
                _ => "⚪"
            };

            sb.AppendLine($"**{guide.GuideId}: {guide.ProblemType}** {difficultyIcon}");
            sb.AppendLine($"  {guide.Description}");
            sb.AppendLine($"  ⏱️ {FormatDuration(guide.Duration)} | 🎯 {guide.Difficulty} | 📝 {guide.Steps.Count} steps");
            sb.AppendLine($"  **Get guide:** Ask me 'Show {key} troubleshooting guide'");
            sb.AppendLine();
        }

        sb.AppendLine("**Need Help Choosing?**");
        sb.AppendLine("  • No connection at all? → Use `no-internet` guide");
        sb.AppendLine("  • Slow speeds? → Use `slow-connection` guide");
        sb.AppendLine("  • WiFi problems? → Use `wifi-issues` guide");
        sb.AppendLine();
        sb.AppendLine("💡 **Pro Tip:** Run diagnostics first to identify your issue!");
        sb.AppendLine("Ask me: 'Run diagnostics'");

        return sb.ToString();
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private static string GetSpeedRating(double actual, double expected)
    {
        double percentage = (actual / expected) * 100;
        return percentage switch
        {
            >= 90 => "✅ Excellent",
            >= 75 => "🟢 Good",
            >= 50 => "🟡 Fair",
            >= 25 => "🟠 Poor",
            _ => "🔴 Critical"
        };
    }

    private static string GetLatencyRating(int latency) => latency switch
    {
        < 20 => "✅ Excellent",
        < 50 => "🟢 Good",
        < 100 => "🟡 Fair",
        < 150 => "🟠 Poor",
        _ => "🔴 Critical"
    };

    private static string GetPacketLossRating(double loss) => loss switch
    {
        < 0.5 => "✅ Excellent",
        < 1.0 => "🟢 Good",
        < 2.0 => "🟡 Fair",
        < 5.0 => "🟠 Poor",
        _ => "🔴 Critical"
    };

    private static string GetJitterRating(double jitter) => jitter switch
    {
        < 5 => "✅ Excellent",
        < 15 => "🟢 Good",
        < 30 => "🟡 Fair",
        < 50 => "🟠 Poor",
        _ => "🔴 Critical"
    };

    private static string FormatDuration(EstimatedDuration duration) => duration switch
    {
        EstimatedDuration.FiveMinutes => "~5 minutes",
        EstimatedDuration.TenMinutes => "~10 minutes",
        EstimatedDuration.FifteenMinutes => "~15 minutes",
        EstimatedDuration.ThirtyMinutes => "~30 minutes",
        _ => "Variable"
    };
}
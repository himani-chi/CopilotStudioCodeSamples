// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace ConsoleCopilotStudio;

/// <summary>
/// Handles console chat with the Copilot Studio Agent + intercepts connector consent cards.
/// </summary>
internal class ChatConsoleService : IHostedService
{
    private readonly CopilotClient _copilotClient;

    // Dedupe: skip repeated identical consent cards in the same conversation
    private readonly HashSet<string> _handledConsentCards = new(StringComparer.Ordinal);

    public ChatConsoleService(CopilotClient copilotClient)
    {
        _copilotClient = copilotClient;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.Write("\nagent> ");

        // 1) Start conversation and print all initial activities
        await foreach (Activity act in _copilotClient.StartConversationAsync(
                           emitStartConversationEvent: true,
                           cancellationToken: cancellationToken))
        {
            System.Diagnostics.Trace.WriteLine($">>>>MessageLoop Duration: {sw.Elapsed.ToDurationString()}");
            sw.Restart();

            if (act is null) throw new InvalidOperationException("Activity is null");
            await PrintActivityAsync(act, cancellationToken);
        }

        // 2) Main Q/A loop
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("\nuser> ");
            string question = Console.ReadLine()!;
            Console.Write("\nagent> ");

            sw.Restart();
            await foreach (Activity act in _copilotClient.AskQuestionAsync(question, null, cancellationToken))
            {
                System.Diagnostics.Trace.WriteLine($">>>>MessageLoop Duration: {sw.Elapsed.ToDurationString()}");
                await PrintActivityAsync(act, cancellationToken);
                sw.Restart();
            }
        }

        sw.Stop();
    }

    /// <summary>
    /// Writes activities to console; intercepts and handles connector consent Adaptive Cards.
    /// </summary>
    private async Task PrintActivityAsync(IActivity act, CancellationToken ct)
    {
        var handledConsentInThisActivity = false;

        switch (act.Type)
        {
            case "message":
                // Text / markdown
                if (string.Equals(act.TextFormat, "markdown", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(act.Text);
                    if (act.SuggestedActions?.Actions.Count > 0)
                    {
                        Console.WriteLine("Suggested actions:\n");
                        act.SuggestedActions.Actions.ToList()
                           .ForEach(action => Console.WriteLine("\t" + action.Text));
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(act.Text))
                        Console.Write($"\n{act.Text}\n");
                }

                // Look for Adaptive Cards
                if (act is Activity msg && msg.Attachments != null && msg.Attachments.Count > 0)
                {
                    foreach (var att in msg.Attachments)
                    {
                        if (!string.Equals(att.ContentType, "application/vnd.microsoft.card.adaptive",
                                           StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (att.Content == null) continue;

                        // Parse card JSON
                        if (!ConsentHelpers.TryParseAdaptiveCard(att.Content, out var cardJson))
                            continue;

                        // Debug: raw card JSON
                        Console.WriteLine("\n[AdaptiveCard JSON]");
                        Console.WriteLine(cardJson.ToString(Newtonsoft.Json.Formatting.Indented));

                        // Dedupe identical card payloads
                        var cardSig = cardJson.ToString(Newtonsoft.Json.Formatting.None);
                        if (_handledConsentCards.Contains(cardSig))
                        {
                            Console.WriteLine("[info] Skipping duplicate consent card.");
                            continue;
                        }

                        // Detect consent card and handle once per activity
                        if (!handledConsentInThisActivity && ConsentHelpers.IsConsentCard(cardJson))
                        {
                            handledConsentInThisActivity = true;
                            _handledConsentCards.Add(cardSig);

                            var svc = ConsentHelpers.ExtractServiceName(cardJson);
                            Console.WriteLine("\n Connector Consent Detected:");
                            Console.WriteLine($"Service: {svc}");

                            // Ask user
                            var defaultChoice = (Environment.GetEnvironmentVariable("COPILOT_AUTO_CONSENT") ?? "Allow");
                            Console.Write($"Allow or Cancel? [A/c] (default: {defaultChoice}): ");
                            var input = Console.ReadLine();
                            var userChoice = string.IsNullOrWhiteSpace(input)
                                ? (defaultChoice.Equals("Cancel", StringComparison.OrdinalIgnoreCase) ? "Cancel" : "Allow")
                                : (input.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? "Cancel" : "Allow");

                            // Get Allow action + build the postBack activity
                            var (allowAction, actionId) = ConsentHelpers.TryGetAllowAction(cardJson);

                            Console.WriteLine("\n[Allow Action JSON from card]");
                            Console.WriteLine(allowAction != null
                                ? allowAction.ToString(Newtonsoft.Json.Formatting.Indented)
                                : "(null)");

                            var consentActivity =
                                ConsentHelpers.BuildMessagePostBackActivity(userChoice, allowAction, actionId);

                            Console.WriteLine("[info] Consent postback mode: Message");

                            await foreach (var reply in _copilotClient.AskQuestionAsync(consentActivity, ct))
                            {
                                await PrintActivityAsync(reply, ct);
                            }

                            break;
                        }
                    }
                }
                break;

            case "typing":
                Console.Write(".");
                break;

            case "event":
                Console.Write("+");
                break;

            case "endOfConversation":
                Console.Write("EOC");
                break;

            default:
                // Fallback logger
                var json = JsonSerializer.Serialize(act, new JsonSerializerOptions { WriteIndented = true });
                Console.Write($"[{act.Type}] {json}\n");
                break;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        System.Diagnostics.Trace.TraceInformation("Stopping");
        return Task.CompletedTask;
    }
}

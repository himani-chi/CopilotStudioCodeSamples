using Microsoft.Agents.Core.Models;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ConsoleCopilotStudio
{
    public static class ConsentHelpers
    {
        // Normalize attachment content to JToken
        public static bool TryParseAdaptiveCard(object content, out JToken json)
        {
            try
            {
                json = content switch
                {
                    string s => JToken.Parse(s),
                    JToken jt => jt,
                    JsonElement je => JToken.Parse(je.GetRawText()),
                    _ => JToken.FromObject(content)
                };
                return true;
            }
            catch
            {
                json = null!;
                return false;
            }
        }

        public static bool IsConsentCard(JToken card)
        {
            bool hasConnectPhrase = card
                .SelectTokens("$.body[?(@.type == 'TextBlock')].text")
                .Any(t => t?.ToString().Contains("Connect to continue", System.StringComparison.OrdinalIgnoreCase) == true);

            var actionTitles = card
                .SelectTokens("$.body..actions[?(@.type == 'Action.Submit')].title")
                .Select(t => t.ToString())
                .ToList();

            bool hasAllowCancel =
                actionTitles.Any(t => t.Equals("Allow", System.StringComparison.OrdinalIgnoreCase)) &&
                actionTitles.Any(t => t.Equals("Cancel", System.StringComparison.OrdinalIgnoreCase));

            return hasConnectPhrase && hasAllowCancel;
        }

        public static string SummarizeConsentCard(JToken card)
        {
            var sb = new StringBuilder();

            var service =
                card.SelectTokens("$.body[?(@.weight == 'Bolder')].text").Select(t => t.ToString())
                    .Concat(card.SelectTokens("$.body..items[?(@.weight == 'Bolder')].text").Select(t => t.ToString()))
                    .FirstOrDefault(s => !s.Equals("Connect to continue", System.StringComparison.OrdinalIgnoreCase))
                ?? "(unknown service)";
            sb.AppendLine($"Service: {service}");

            var capabilities = card.SelectTokens("$.body[?(@.id =~ /capability/i)].text")
                                   .Select(t => t.ToString()).ToList();
            if (capabilities.Count == 0)
            {
                var texts = card.SelectTokens("$.body[*].text").Select(t => t?.ToString()).ToList();
                var idx = texts.FindIndex(x => x != null && x.Contains("This connection can:", System.StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    for (int i = idx + 1; i < texts.Count; i++)
                    {
                        var v = texts[i];
                        if (!string.IsNullOrWhiteSpace(v) && (v.StartsWith("-") || v.StartsWith("•") || v.StartsWith("—")))
                            capabilities.Add(v);
                        else if (!string.IsNullOrWhiteSpace(v)) break;
                    }
                }
            }
            if (capabilities.Count > 0)
            {
                sb.AppendLine("Capabilities:");
                foreach (var c in capabilities) sb.AppendLine($"  {c}");
            }

            var warning = card.SelectTokens("$.body[?(@.isSubtle == true)].text")
                              .Select(t => t.ToString()).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(warning))
                sb.AppendLine($"Warning: {warning}");

            return sb.ToString().TrimEnd();
        }

        // Pull the Allow action and its id
        public static (JObject? allowAction, string actionId) TryGetAllowAction(JToken card)
        {
            var allowAction = card.SelectTokens("$..actions[?(@.type=='Action.Submit')]")
                                  .OfType<JObject>()
                                  .FirstOrDefault(a => string.Equals(a["title"]?.ToString(), "Allow", System.StringComparison.OrdinalIgnoreCase));

            var id = allowAction?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) id = "submit";
            return (allowAction, id!);
        }

        private static Dictionary<string, object?> MergeAllowData(string choice, JObject? allowActionJObj, string actionId)
        {
            var id = string.IsNullOrWhiteSpace(actionId) ? "submit" : actionId;

            var merged = new Dictionary<string, object?>
            {
                ["action"] = choice,                // "Allow" | "Cancel"
                ["id"] = id,
                ["shouldAwaitUserInput"] = true
            };

            var allowData = allowActionJObj?["data"] as JObject;
            if (allowData != null)
            {
                foreach (var p in allowData.Properties())
                {
                    if (!merged.ContainsKey(p.Name))
                        merged[p.Name] = p.Value?.ToObject<object?>();
                }
            }

            return merged;
        }

        public static Activity BuildMessagePostBackActivity(string choice, JObject? allowActionJObj, string actionId)
        {
            var merged = MergeAllowData(choice, allowActionJObj, actionId);

            return new Activity
            {
                Type = ActivityTypes.Message,
                ChannelData = new { postBack = true }, // minimal per guidance
                Value = merged
            };
        }

        public static string ExtractServiceName(JToken card)
        {
            var colName = card.SelectTokens("$.body[?(@.type=='ColumnSet')].columns[*].items[?(@.type=='TextBlock')].text")
                              .Select(t => t.ToString())
                              .FirstOrDefault(t => !t.Equals("Connect to continue", System.StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(colName)) return colName;

            var bold = card.SelectTokens("$..[?(@.type=='TextBlock' && @.weight=='Bolder')].text")
                           .Select(t => t.ToString())
                           .FirstOrDefault(t => !t.Equals("Connect to continue", System.StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(bold)) return bold;

            return "(unknown service)";
        }
    }
}

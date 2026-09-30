using System;
using System.Collections.Generic;

namespace POps.Shared
{
    public sealed class VisionAuthSelection
    {
        public IReadOnlyDictionary<string, string> Headers { get; init; }
        public bool CanConnect { get; init; }
    }

    public sealed class VisionCloseDecision
    {
        public bool AuthenticationRejected { get; init; }
        public bool ClearStream { get; init; }
        public bool ClearApproval { get; init; }
        public bool RetryAutomatically { get; init; }
    }

    public static class VisionChannel
    {
        // Vision hiçbir zaman kayıt jetonu taşımaz. Jeton yalnız komut soketindeki ilk kayıt içindir.
        public static VisionAuthSelection SelectHeaders(string deviceSecret, string enrollToken, string agentVersion)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(agentVersion)) headers["X-Agent-Version"] = agentVersion;
            if (!string.IsNullOrEmpty(deviceSecret)) headers["X-Agent-Secret"] = deviceSecret;
            return new VisionAuthSelection { Headers = headers, CanConnect = !string.IsNullOrEmpty(deviceSecret) };
        }

        public static VisionCloseDecision OnClosed(int? closeStatus) => new VisionCloseDecision
        {
            AuthenticationRejected = closeStatus == 4401,
            ClearStream = true,
            ClearApproval = true,
            RetryAutomatically = false,
        };
    }
}

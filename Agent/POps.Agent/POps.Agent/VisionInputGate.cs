namespace POpsAgent
{
    public static class VisionInputGate
    {
        // null: iletilebilir; "vision" / "consent": capability_denied nedeni.
        public static string? DenialReason(bool visionEnabled, bool sessionApproved, bool channelConnected, bool isInputEvent)
        {
            if (!visionEnabled) return "vision";
            if (isInputEvent && (!sessionApproved || !channelConnected)) return "consent";
            return null;
        }
    }
}

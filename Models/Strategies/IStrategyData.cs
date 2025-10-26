using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies
{
    public interface IStrategyData
    {
        // identity / legacy
        string    Name { get; set; }
        Direction TriggeredDirection { get; set; }

        // legacy trigger flag
        bool      StrategyTriggered { get; set; }
        bool      IsTriggered { get; }   // convenience alias used by host

        // managed-entry proposal (used when ExecMode == Managed)
        EntryExecMode ExecMode { get; set; }
        double?       ProposedStopPrice   { get; set; }
        double?       ProposedTargetPrice { get; set; }
        string        ProposedEntryTag    { get; set; }

        // --- NEW: quantity visible to the host/executor ---
        int PendingQuantity { get; }     // StrategyData implements { get; set; }

        // legacy 2-arg trigger (ATM path)
        void UpdateTriggeredDataProvider(Direction triggeredDirection, bool strategyTriggered);

        // managed proposals (no quantity)
        void UpdateTriggeredDataProvider(
            Direction     triggeredDirection,
            bool          strategyTriggered,
            double        stopPrice,
            double        targetPrice,
            string        entryTag,
            EntryExecMode execMode);

        // --- NEW: managed proposals WITH quantity ---
        void UpdateTriggeredDataProvider(
            Direction     triggeredDirection,
            bool          strategyTriggered,
            double        stopPrice,
            double        targetPrice,
            string        entryTag,
            EntryExecMode execMode,
            int           quantity);

        void ClearProposed();
    }
}

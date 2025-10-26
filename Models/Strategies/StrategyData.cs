using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies
{
    // execution mode (ATM or fully managed by the host)
    public enum EntryExecMode
    {
        ATM     = 0,
        Managed = 1
    }

    /// <summary>
    /// Shared data used by a strategy to request an entry (ATM or Managed) from the host.
    /// </summary>
    public class StrategyData : IStrategyData
    {
        // ---- Identity / legacy ----
        public string    Name               { get; set; }
        public Direction TriggeredDirection { get; set; }

        // ---- Trigger flag ----
        public bool StrategyTriggered { get; set; }
        public bool IsTriggered { get { return StrategyTriggered; } }

        // ---- Managed-entry proposal ----
        public EntryExecMode ExecMode { get; set; }
        public double?       ProposedStopPrice   { get; set; }
        public double?       ProposedTargetPrice { get; set; }
        public string        ProposedEntryTag    { get; set; }

        // ---- Quantity for executor (default via ctor to avoid CS1519) ----
        public int PendingQuantity { get; set; }

        public StrategyData()
        {
            PendingQuantity = 1; // safe default (no auto-property initializer)
        }

        public StrategyData(string name, Direction triggeredDirection, bool strategyTriggered)
        {
            Name               = name;
            TriggeredDirection = triggeredDirection;
            StrategyTriggered  = strategyTriggered;
            PendingQuantity    = 2;
        }

        // ===== Interface-required minimal stub =====
        // IStrategyData.UpdateTriggeredDataProvider(Direction, bool)
        public void UpdateTriggeredDataProvider(Direction direction, bool trigger)
        {
            TriggeredDirection = direction;
            StrategyTriggered  = trigger;
            // leave proposed prices/tag/mode as-is; call ClearProposed() here if you prefer to reset
            // ClearProposed();
        }

        // ===== Rich overload with quantity =====
        public void UpdateTriggeredDataProvider(
            Direction     direction,
            bool          trigger,
            double        stopPrice,
            double        targetPrice,
            string        tag,
            EntryExecMode mode,
            int           quantity)
        {
            TriggeredDirection  = direction;
            StrategyTriggered   = trigger;
            ExecMode            = mode;

            ProposedStopPrice   = stopPrice;
            ProposedTargetPrice = targetPrice;
            ProposedEntryTag    = tag;

            PendingQuantity     = quantity; // consumed by host/executor when placing the order
        }

        // ===== Back-compat overload (no quantity) =====
        public void UpdateTriggeredDataProvider(
            Direction     triggeredDirection,
            bool          strategyTriggered,
            double        stopPrice,
            double        targetPrice,
            string        entryTag,
            EntryExecMode execMode)
        {
            TriggeredDirection  = triggeredDirection;
            StrategyTriggered   = strategyTriggered;
            ExecMode            = execMode;

            ProposedStopPrice   = stopPrice;
            ProposedTargetPrice = targetPrice;
            ProposedEntryTag    = entryTag;
            // PendingQuantity remains whatever was last set (default 1)
        }

        public void ClearProposed()
        {
            ProposedStopPrice   = null;
            ProposedTargetPrice = null;
            ProposedEntryTag    = null;
        }
    }
}

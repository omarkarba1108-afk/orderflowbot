using System;
using System.Collections.Generic;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class StrategiesEvents
    {
        private readonly EventManager _eventManager;

        // Hold a single StrategyData instance, but expose it via the interface
        private readonly IStrategyData _strategyData = new StrategyData();

        // Read-only accessor (compatible with older C#)
        public IStrategyData StrategyData
        {
            get { return _strategyData; }
        }

        public event Func<List<StrategyBase>> OnGetStrategies;
        public event Action OnResetStrategyData;

        public StrategiesEvents(EventManager eventManager)
        {
            _eventManager = eventManager;
        }

        /// <summary>Event triggered for getting the strategies.</summary>
        public List<StrategyBase> GetStrategies()
        {
            return _eventManager.InvokeEvent<List<StrategyBase>>(delegate
            {
                if (OnGetStrategies != null)
                    return OnGetStrategies();

                _eventManager.PrintMessage("OnGetStrategies handler is null");
                return new List<StrategyBase>();
            });
        }

        /// <summary>Reset StrategyData and notify listeners.</summary>
        public void ResetStrategyData()
        {
            // Clear any pending managed proposal so stale orders don't fire
            if (_strategyData != null)
                _strategyData.ClearProposed();

            _eventManager.InvokeEvent(OnResetStrategyData);
        }
    }
}

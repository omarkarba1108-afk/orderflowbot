using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies;
using NinjaTrader.Custom.AddOns.OrderFlowBot.States;
using System;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class TradingEvents
    {
        private readonly EventManager _eventManager;
        public event Func<IReadOnlyTradingState> OnGetTradingState;
        public event Action<IStrategyData> OnStrategyTriggered;
        public event Action OnStrategyTriggeredProcessed;
        public event Action OnResetTriggeredTradingState;
        public event Action OnCloseTriggered;
        public event Action<int> OnLastTradedBarNumberTriggered;
        public event Action<int> OnCurrentBarNumberTriggered;
        public event Action<bool> OnMarketPositionTriggered;
        public event Action OnResetTriggerStrikePrice;
        public event Action OnTriggerStrikePriceTriggered;
        public event Action OnResetSelectedTradeDirection;
        public event Action OnPositionClosedWithAutoDisabled;

        public TradingEvents(EventManager eventManager)
        {
            _eventManager = eventManager;
        }

        /// <summary>Get read-only trading state.</summary>
        public IReadOnlyTradingState GetTradingState()
        {
            // Avoid null-conditional invocation (unsupported)
            return _eventManager.InvokeEvent<IReadOnlyTradingState>(delegate
            {
                if (OnGetTradingState != null)
                    return OnGetTradingState();

                _eventManager.PrintMessage("OnGetTradingState handler is null");
                return null;
            });
        }

        public void StrategyTriggered(IStrategyData strategyTriggeredData)
        {
            _eventManager.InvokeEvent(OnStrategyTriggered, strategyTriggeredData);
        }

        public void StrategyTriggeredProcessed()
        {
            _eventManager.InvokeEvent(OnStrategyTriggeredProcessed);
        }

        public void ResetTriggeredTradingState()
        {
            _eventManager.InvokeEvent(OnResetTriggeredTradingState);
        }

        public void CloseTriggered()
        {
            _eventManager.InvokeEvent(OnCloseTriggered);
        }

        public void LastTradedBarNumberTriggered(int barNumber)
        {
            _eventManager.InvokeEvent(OnLastTradedBarNumberTriggered, barNumber);
        }

        public void CurrentBarNumberTriggered(int barNumber)
        {
            _eventManager.InvokeEvent(OnCurrentBarNumberTriggered, barNumber);
        }

        public void MarketPositionTriggered(bool hasMarketPosition)
        {
            _eventManager.InvokeEvent(OnMarketPositionTriggered, hasMarketPosition);
        }

        public void ResetTriggerStrikePrice()
        {
            _eventManager.InvokeEvent(OnResetTriggerStrikePrice);
        }

        public void TriggerStrikePriceTriggered()
        {
            _eventManager.InvokeEvent(OnTriggerStrikePriceTriggered);
        }

        public void ResetSelectedTradeDirection()
        {
            _eventManager.InvokeEvent(OnResetSelectedTradeDirection);
        }

        public void PositionClosedWithAutoDisabled()
        {
            _eventManager.InvokeEvent(OnPositionClosedWithAutoDisabled);
        }
    }
}

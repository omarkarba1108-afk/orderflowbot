using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using System;
using System.Collections.Generic;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class DataBarEvents
    {
        private readonly EventManager _eventManager;
        public event Action<IDataBarDataProvider> OnUpdateCurrentDataBar;
        public event Action OnUpdateDataBarList;
        public event Action<IDataBarPrintConfig> OnPrintDataBar;
        public event Action OnUpdatedCurrentDataBar;
        public event Func<IReadOnlyDataBar> OnGetCurrentDataBar;
        public event Func<List<IReadOnlyDataBar>> OnGetDataBars;

        public DataBarEvents(EventManager eventManager)
        {
            _eventManager = eventManager;
        }

        public void UpdateCurrentDataBar(IDataBarDataProvider dataProvider)
        {
            _eventManager.InvokeEvent(OnUpdateCurrentDataBar, dataProvider);
        }

        public void UpdateDataBarList()
        {
            _eventManager.InvokeEvent(OnUpdateDataBarList);
        }

        public void UpdatedCurrentDataBar()
        {
            _eventManager.InvokeEvent(OnUpdatedCurrentDataBar);
        }

        public IReadOnlyDataBar GetCurrentDataBar()
        {
            // avoid ?. — do null check inside the delegate
            return _eventManager.InvokeEvent<IReadOnlyDataBar>(delegate
            {
                if (OnGetCurrentDataBar != null)
                    return OnGetCurrentDataBar();
                _eventManager.PrintMessage("OnGetCurrentDataBar handler is null");
                return null;
            });
        }

        public List<IReadOnlyDataBar> GetDataBars()
        {
            // avoid ?. — do null check inside the delegate
            return _eventManager.InvokeEvent<List<IReadOnlyDataBar>>(delegate
            {
                if (OnGetDataBars != null)
                    return OnGetDataBars();
                _eventManager.PrintMessage("OnGetDataBars handler is null");
                return new List<IReadOnlyDataBar>();
            });
        }

        public void PrintDataBar(IDataBarPrintConfig config)
        {
            _eventManager.InvokeEvent(OnPrintDataBar, config);
        }
    }
}

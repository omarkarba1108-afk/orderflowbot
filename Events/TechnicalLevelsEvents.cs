using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.TechnicalLevelsModel;
using System;
using System.Collections.Generic;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class TechnicalLevelsEvents
    {
        private readonly EventManager _eventManager;
        public event Action<ITechnicalLevelsDataProvider> OnUpdateCurrentTechnicalLevels;
        public event Action OnUpdateTechnicalLevelsList;
        public event Action<ITechnicalLevelsPrintConfig> OnPrintTechnicalLevels;
        public event Action OnUpdatedCurrentTechnicalLevels;
        public event Func<IReadOnlyTechnicalLevels> OnGetCurrentTechnicalLevels;
        public event Func<List<IReadOnlyTechnicalLevels>> OnGetTechnicalLevelsList;

        public TechnicalLevelsEvents(EventManager eventManager)
        {
            _eventManager = eventManager;
        }

        public void UpdateCurrentTechnicalLevels(ITechnicalLevelsDataProvider dataProvider)
        {
            _eventManager.InvokeEvent(OnUpdateCurrentTechnicalLevels, dataProvider);
        }

        public void UpdateTechnicalLevelsList()
        {
            _eventManager.InvokeEvent(OnUpdateTechnicalLevelsList);
        }

        public void UpdatedCurrentTechnicalLevels()
        {
            _eventManager.InvokeEvent(OnUpdatedCurrentTechnicalLevels);
        }

        public IReadOnlyTechnicalLevels GetCurrentTechnicalLevels()
        {
            return _eventManager.InvokeEvent<IReadOnlyTechnicalLevels>(delegate
            {
                if (OnGetCurrentTechnicalLevels != null)
                    return OnGetCurrentTechnicalLevels();

                _eventManager.PrintMessage("OnGetCurrentTechnicalLevels handler is null");
                return null;
            });
        }

        public List<IReadOnlyTechnicalLevels> GetTechnicalLevelsList()
        {
            return _eventManager.InvokeEvent<List<IReadOnlyTechnicalLevels>>(delegate
            {
                if (OnGetTechnicalLevelsList != null)
                    return OnGetTechnicalLevelsList();

                _eventManager.PrintMessage("OnGetTechnicalLevelsList handler is null");
                return new List<IReadOnlyTechnicalLevels>();
            });
        }

        public void PrintTechnicalLevels(ITechnicalLevelsPrintConfig config)
        {
            _eventManager.InvokeEvent(OnPrintTechnicalLevels, config);
        }
    }
}

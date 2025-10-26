using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.TechnicalLevelsModel;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.TradeAnalysis.StackedImbalances;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json; // switched from System.Text.Json

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Services
{
    public class TradeAnalysisService
    {
        private readonly EventsContainer _eventsContainer;
        private readonly string _directoryPath;
        private readonly JsonSerializerSettings _jsonSettings;
        private TradeData _trainingTradeData;
        private readonly int _barsToExtract;

        public TradeAnalysisService(EventsContainer eventsContainer)
        {
            _eventsContainer = eventsContainer;
            _directoryPath = DataBarConfig.Instance.TrainingDataDirectory;
            _trainingTradeData = null;
            _barsToExtract = 3;

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Include
            };

            eventsContainer.TradeAnalysisEvents.OnAddTrainingEntry += HandleAddTrainingEntry;
            eventsContainer.TradeAnalysisEvents.OnAddTrainingExit += HandleAddTrainingExit;
            eventsContainer.TradeAnalysisEvents.OnGetTradeData += HandleGetTradeData;
        }

        #region Training

        private string GetCurrentDateBasedFilePath()
        {
            IReadOnlyDataBar currentBar = _eventsContainer.DataBarEvents.GetCurrentDataBar();
            string fileName = currentBar.Day + ".json";
            return Path.Combine(_directoryPath, fileName);
        }

        private void HandleAddTrainingEntry(TradeType tradeType)
        {
            if (_trainingTradeData == null)
            {
                IReadOnlyDataBar dataBar = _eventsContainer.DataBarEvents.GetCurrentDataBar();
                List<IReadOnlyDataBar> dataBars = _eventsContainer.DataBarEvents.GetDataBars();
                IReadOnlyTechnicalLevels currentTechnicalLevels = _eventsContainer.TechnicalLevelsEvents.GetCurrentTechnicalLevels();
                List<IReadOnlyTechnicalLevels> readOnlyTechnicalLevelsList = _eventsContainer.TechnicalLevelsEvents.GetTechnicalLevelsList();

                List<IReadOnlyDataBar> extractedDataBars = dataBars.Skip(Math.Max(0, dataBars.Count - _barsToExtract)).ToList();
                List<IReadOnlyTechnicalLevels> extractedTechnicalLevelsList = readOnlyTechnicalLevelsList.Skip(Math.Max(0, readOnlyTechnicalLevelsList.Count - _barsToExtract)).ToList();

                TradeData tradeData = new TradeData
                {
                    TradeDirection = (int)tradeType,
                    PreTradeBars = GetPreTradeBars(TradeStatus.None, extractedDataBars, extractedTechnicalLevelsList),
                    CurrentBar = GetTradeBar(TradeStatus.Entry, dataBar, currentTechnicalLevels)
                };

                _trainingTradeData = tradeData;
            }
        }

        private void HandleAddTrainingExit(int winLoss)
        {
            if (_trainingTradeData == null)
                return;

            _trainingTradeData.TradeOutcome = winLoss;

            string filePath = GetCurrentDateBasedFilePath();
            Directory.CreateDirectory(_directoryPath);

            List<TradeData> existingData;
            if (!File.Exists(filePath))
            {
                existingData = new List<TradeData>();
            }
            else
            {
                string fileContent = File.ReadAllText(filePath);
                existingData = JsonConvert.DeserializeObject<List<TradeData>>(fileContent) ?? new List<TradeData>();
            }

            existingData.Add(_trainingTradeData);
            string jsonData = JsonConvert.SerializeObject(existingData, _jsonSettings);
            File.WriteAllText(filePath, jsonData);

            _trainingTradeData = null;
        }

        private List<TradeBar> GetPreTradeBars(
            TradeStatus tradeStatus,
            List<IReadOnlyDataBar> dataBars,
            List<IReadOnlyTechnicalLevels> readOnlyTechnicalLevelsList
        )
        {
            List<TradeBar> tradeBars = new List<TradeBar>();

            for (int i = 0; i < dataBars.Count; i++)
            {
                tradeBars.Add(GetTradeBar(tradeStatus, dataBars[i], readOnlyTechnicalLevelsList[i]));
            }

            return tradeBars;
        }

        private TradeBar GetTradeBar(
            TradeStatus tradeStatus,
            IReadOnlyDataBar readOnlyDataBar,
            IReadOnlyTechnicalLevels readOnlyTechnicalLevels
        )
        {
            SignalBar dataBar = null;
            SignalBar deltaBar = null;
            OrderFlowData orderFlowData = null;

            if (tradeStatus == TradeStatus.None || tradeStatus == TradeStatus.Entry)
            {
                dataBar = new SignalBar();
                dataBar.Update(
                    readOnlyDataBar,
                    readOnlyTechnicalLevels,
                    SignalBarType.DataBar
                );

                deltaBar = new SignalBar();
                deltaBar.Update(
                    readOnlyDataBar,
                    readOnlyTechnicalLevels,
                    SignalBarType.DeltaBar
                );

                orderFlowData = new OrderFlowData(readOnlyDataBar);
            }

            return new TradeBar
            {
                DataBar = dataBar,
                DeltaBar = deltaBar,
                OrderFlowData = orderFlowData
            };
        }

        #endregion

        #region Live

        private string HandleGetTradeData(
            TradeType tradeType,
            List<IReadOnlyDataBar> dataBars,
            IReadOnlyDataBar currentDataBar
        )
        {
            IReadOnlyTechnicalLevels currentTechnicalLevels = _eventsContainer.TechnicalLevelsEvents.GetCurrentTechnicalLevels();
            List<IReadOnlyTechnicalLevels> readOnlyTechnicalLevelsList = _eventsContainer.TechnicalLevelsEvents.GetTechnicalLevelsList();

            List<IReadOnlyDataBar> extractedDataBars = dataBars.Skip(Math.Max(0, dataBars.Count - _barsToExtract)).ToList();
            List<IReadOnlyTechnicalLevels> extractedTechnicalLevelsList = readOnlyTechnicalLevelsList.Skip(Math.Max(0, readOnlyTechnicalLevelsList.Count - _barsToExtract)).ToList();

            var filteredData = new
            {
                TradeDirection = (int)tradeType,
                PreTradeBars = GetLivePreTradeBars(extractedDataBars, extractedTechnicalLevelsList),
                CurrentBar = GetLiveTradeBar(currentDataBar, currentTechnicalLevels)
            };

            return JsonConvert.SerializeObject(filteredData, _jsonSettings);
        }

        private List<TradeBar> GetLivePreTradeBars(List<IReadOnlyDataBar> dataBars, List<IReadOnlyTechnicalLevels> readOnlyTechnicalLevels)
        {
            List<TradeBar> tradeBars = new List<TradeBar>();

            for (int i = 0; i < dataBars.Count; i++)
            {
                tradeBars.Add(GetLiveTradeBar(dataBars[i], readOnlyTechnicalLevels[i]));
            }

            return tradeBars;
        }

        private TradeBar GetLiveTradeBar(IReadOnlyDataBar readOnlyDataBar, IReadOnlyTechnicalLevels readOnlyTechnicalLevels)
        {
            SignalBar dataBar = new SignalBar();
            dataBar.Update(
                readOnlyDataBar,
                readOnlyTechnicalLevels,
                SignalBarType.DataBar
            );

            SignalBar deltaBar = new SignalBar();
            deltaBar.Update(
                readOnlyDataBar,
                readOnlyTechnicalLevels,
                SignalBarType.DeltaBar
            );

            OrderFlowData orderFlowData = new OrderFlowData(readOnlyDataBar);

            return new TradeBar
            {
                DataBar = dataBar,
                DeltaBar = deltaBar,
                OrderFlowData = orderFlowData
            };
        }

        #endregion
    }
}

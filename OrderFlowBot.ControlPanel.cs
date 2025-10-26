using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Events;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Services;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Utils;
using NinjaTrader.Gui.Chart;
using System;
using System.Windows;
using System.Windows.Controls;

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class OrderFlowBot : Strategy
    {
        private ChartTab _chartTab;
        private Chart _chartWindow;
        private Grid _chartTraderGrid;

        // Our UI
        private Grid _mainGrid;
        private ScrollViewer _ofbScroll;
        private TextBlock _orderFlowLabel;
        private TradeManagementGrid _tradeManagementGrid;
        private StrategiesGrid _strategiesGrid;

        private bool _panelActive;
        private TabItem _tabItem;

        // Track which row in ChartTrader we're using
        private int _panelRowIndex = -1;

        // Events / services
        private UserInterfaceEvents _userInterfaceEvents;
        private UserInterfaceService _userInterfaceService;

        public void InitializeUIManager()
        {
            LoadControlPanel();
        }

        private void LoadControlPanel()
        {
            if (ChartControl == null) return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                _userInterfaceEvents = new UserInterfaceEvents(_eventManager);

                CreateWPFControls();

                _userInterfaceService = new UserInterfaceService(
                    _servicesContainer,
                    _userInterfaceEvents,
                    _tradeManagementGrid,
                    _strategiesGrid
                );
            });
        }

        private void UnloadControlPanel()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(DisposeWPFControls);
        }

        private void ReadyControlPanel()
        {
            if (ChartControl == null) return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                _userInterfaceEvents.OnUpdateControlPanelLabel += UpdateControlPanelLabel;

                if (!BacktestEnabled)
                {
                    if (TimeEnabled && !_validTimeRange)
                    {
                        UpdateValidStartEndTimeUserInterface(false);
                    }
                    else if (ValidDailyProfitLossHit())
                    {
                        UpdateDailyProfitLossUserInterface();
                    }
                    else
                    {
                        UpdateControlPanelLabel("Coq 911");

                        _tradeManagementGrid.Ready();
                        _strategiesGrid.Ready();
                    }

                    // Enable the grids
                    _tradeManagementGrid.IsEnabled = true;
                    _strategiesGrid.IsEnabled = true;
                }
                else
                {
                    UpdateControlPanelLabel("Backtesting");
                }
            });
        }

        private void UpdateControlPanelLabel(string text)
        {
            if (_orderFlowLabel == null) return;

            if (_orderFlowLabel.Dispatcher.CheckAccess())
                HandleUpdateControlPanelLabel(text);
            else
                _orderFlowLabel.Dispatcher.Invoke(() => HandleUpdateControlPanelLabel(text));
        }

        private void HandleUpdateControlPanelLabel(string text)
        {
            _orderFlowLabel.Text = text;
        }

        private void CreateWPFControls()
        {
            // Get the chart window and the ChartTrader grid
            _chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;
            if (_chartWindow == null) return;

            var chartTrader = _chartWindow.FindFirst("ChartWindowChartTraderControl") as ChartTrader;
            _chartTraderGrid = chartTrader != null ? chartTrader.Content as Grid : null;
            if (_chartTraderGrid == null) return;

            // === Build the OrderFlowBot panel content ===

            _mainGrid = new Grid
            {
                Margin = new Thickness(0, 6, 0, 6),
                Background = UserInterfaceUtils.GetSolidColorBrushFromHex(CustomColors.MAIN_GRID_BG_COLOR)
            };

            // 3 rows: Title / Trade Mgmt / Strategies
            _mainGrid.RowDefinitions.Add(new RowDefinition());
            _mainGrid.RowDefinitions.Add(new RowDefinition());
            _mainGrid.RowDefinitions.Add(new RowDefinition());

            // Create a horizontal panel for logo and title
            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };

            // Add logo
            var logoImage = UserInterfaceUtils.CreateLogoImage();
            titlePanel.Children.Add(logoImage);

            // Add title text
            _orderFlowLabel = new TextBlock
            {
                FontFamily = ChartControl.Properties.LabelFont.Family,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = UserInterfaceUtils.GetSolidColorBrushFromHex(CustomColors.TEXT_COLOR),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                Text = "Loading..."
            };
            titlePanel.Children.Add(_orderFlowLabel);

            _tradeManagementGrid = new TradeManagementGrid(_servicesContainer, _userInterfaceEvents) { IsEnabled = false };
            _strategiesGrid      = new StrategiesGrid(_servicesContainer, _userInterfaceEvents, _eventsContainer.StrategiesEvents) { IsEnabled = false };

            Grid.SetRow(titlePanel, 0);
            Grid.SetRow(_tradeManagementGrid, 1);
            Grid.SetRow(_strategiesGrid, 2);

            _mainGrid.Children.Add(titlePanel);
            _mainGrid.Children.Add(_tradeManagementGrid);
            _mainGrid.Children.Add(_strategiesGrid);

            // Wrap panel in a ScrollViewer with a bounded height
            _ofbScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = _mainGrid
            };

            // Target the last row in ChartTrader
            _panelRowIndex = _chartTraderGrid.RowDefinitions.Count - 1;

            // Size binding so scrolling appears when content > available area
            UpdateScrollViewerHeight();
            _chartTraderGrid.SizeChanged += ChartTrader_SizeChanged;

            if (TabSelected())
                InsertWPFControls();

            _chartWindow.MainTabControl.SelectionChanged += TabChangedHandler;
        }

        private void ChartTrader_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateScrollViewerHeight();
        }

        private void UpdateScrollViewerHeight()
        {
            if (_chartTraderGrid == null || _ofbScroll == null || _panelRowIndex < 0) return;

            // Height occupied by rows above ours
            double heightAbove = 0;
            for (int i = 0; i < _panelRowIndex; i++)
                heightAbove += _chartTraderGrid.RowDefinitions[i].ActualHeight;

            double available = _chartTraderGrid.ActualHeight - heightAbove - 6; // small margin

            // Ensure a sensible minimum so scroll can kick in
            _ofbScroll.MaxHeight = (available > 120) ? available : 350;
        }

        private void DisposeWPFControls()
        {
            if (_chartWindow != null)
                _chartWindow.MainTabControl.SelectionChanged -= TabChangedHandler;

            if (_chartTraderGrid != null)
                _chartTraderGrid.SizeChanged -= ChartTrader_SizeChanged;

            RemoveWPFControls();
        }

        private void InsertWPFControls()
        {
            if (_panelActive || _chartTraderGrid == null || _ofbScroll == null) return;

            // Re-evaluate the last row in case ChartTrader layout changed
            _panelRowIndex = _chartTraderGrid.RowDefinitions.Count - 1;

            Grid.SetRow(_ofbScroll, _panelRowIndex);
            _chartTraderGrid.Children.Add(_ofbScroll);
            _panelActive = true;

            // Ensure height is correct right after insertion
            UpdateScrollViewerHeight();
        }

        private void RemoveWPFControls()
        {
            if (!_panelActive || _chartTraderGrid == null || _ofbScroll == null) return;

            _chartTraderGrid.Children.Remove(_ofbScroll);
            _panelActive = false;
        }

        private bool TabSelected()
        {
            if (_chartWindow == null || ChartControl == null) return false;

            foreach (TabItem tab in _chartWindow.MainTabControl.Items)
            {
                var ct = tab.Content as ChartTab;
                if (ct != null && ct.ChartControl == ChartControl && tab == _chartWindow.MainTabControl.SelectedItem)
                    return true;
            }
            return false;
        }

        private void TabChangedHandler(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count <= 0) return;

            _tabItem = e.AddedItems[0] as TabItem;
            if (_tabItem == null) return;

            _chartTab = _tabItem.Content as ChartTab;
            if (_chartTab == null) return;

            if (TabSelected())
                InsertWPFControls();
            else
                RemoveWPFControls();
        }
    }
}

using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Events;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components.Controls;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Events;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components
{
    public abstract class GridBase : Grid, IGrid
    {
        protected readonly ServicesContainer servicesContainer;
        protected readonly UserInterfaceEvents userInterfaceEvents;
        protected readonly StrategiesEvents strategiesEvents;
        protected Dictionary<string, Button> buttons;
        protected Grid grid;

        protected Dictionary<string, bool> initialToggleState;
        private readonly Image _icon;

        protected GridBase(
            string label,
            ServicesContainer servicesContainer,
            UserInterfaceEvents userInterfaceEvents,
            StrategiesEvents strategiesEvents = null,
            Image icon = null)
        {
            this.servicesContainer = servicesContainer;
            this.userInterfaceEvents = userInterfaceEvents;
            this.userInterfaceEvents.OnEnabledDisabledTriggered += HandleEnabledDisabledTriggered;
            this.userInterfaceEvents.OnAutoTradeTriggered += HandleAutoTradeTriggered;
            this.userInterfaceEvents.OnDisableAllControls += HandleDisableAllControls;
            this.userInterfaceEvents.OnEnableAllControls += HandleEnableAllControls;
            this.strategiesEvents = strategiesEvents;

            _icon = icon;

            initialToggleState = new Dictionary<string, bool>();
            buttons = new Dictionary<string, Button>();

            InitializeComponent(label);
        }

        public void InitializeComponent(string label)
        {
            InitializeGrid();
            InitializeInitialToggleState();
            AddHeadingLabel(label);
            AddButtons();
        }

        public virtual void Ready()
        {
            SetAllButtonsEnabled(true);
        }

        protected virtual void InitializeGrid()
        {
            grid = new Grid { Margin = new Thickness(4, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Children.Add(grid);
        }

        public virtual void AddHeadingLabel(string label)
        {
            var headingContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock headingLabel = new TextHeadingLabel(label).Label;
            headingLabel.TextAlignment = TextAlignment.Center;
            headingLabel.HorizontalAlignment = HorizontalAlignment.Stretch;

            headingContainer.Children.Add(headingLabel);
            if (_icon != null) headingContainer.Children.Add(_icon);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(headingContainer);
            Grid.SetColumnSpan(headingContainer, 2);
            Grid.SetRow(headingContainer, 0);
        }

        public abstract void InitializeInitialToggleState();
        protected abstract void AddButtons();
        public abstract void HandleAutoTradeTriggered(bool isEnabled);
        public abstract void HandleButtonClick(object sender, EventArgs e);

       public virtual void HandleEnabledDisabledTriggered(bool isEnabled)
		{
    		SetAllButtonsEnabled(isEnabled);
		}


        protected void SetAllButtonsEnabled(bool isEnabled)
        {
            foreach (var button in buttons.Values)
            {
                if (button.Name == ButtonName.ENABLED && !isEnabled) continue;
                button.IsEnabled = isEnabled;
            }
        }

        protected static void SetButtonEnabled(Button button, bool isEnabled)
			{
    			button.IsEnabled = isEnabled;
			}

        protected virtual void AddButtonToGrid(Button button, int row, int column)
        {
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (!grid.Children.Contains(button))
                grid.Children.Add(button);

            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
        }

        private void HandleDisableAllControls()
        {
            if (Dispatcher.CheckAccess())
                DisableControls(this);
            else
                Dispatcher.BeginInvoke(new Action(delegate { DisableControls(this); }));
        }

        // ======== No C# 7 pattern matching here ========
        private void DisableControls(UIElement uiElement)
        {
            var control = uiElement as Control;
            if (control != null)
            {
                control.IsEnabled = false;
                return;
            }

            var panel = uiElement as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                    DisableControls(child);
                return;
            }

            var contentControl = uiElement as ContentControl;
            if (contentControl != null)
            {
                var content = contentControl.Content as UIElement;
                if (content != null)
                    DisableControls(content);
            }
        }

        private void HandleEnableAllControls()
        {
            if (Dispatcher.CheckAccess())
                EnableControls(this);
            else
                Dispatcher.BeginInvoke(new Action(delegate { EnableControls(this); }));
        }

        private void EnableControls(UIElement uiElement)
        {
            var control = uiElement as Control;
            if (control != null)
            {
                control.IsEnabled = true;
                return;
            }

            var panel = uiElement as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                    EnableControls(child);
                return;
            }

            var contentControl = uiElement as ContentControl;
            if (contentControl != null)
            {
                var content = contentControl.Content as UIElement;
                if (content != null)
                    EnableControls(content);
            }
        }
    }
}

using NinjaTrader.Custom.AddOns.OrderFlowBot.Containers;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components.Controls;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Events;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Models;
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components
{
    public class TradeManagementGrid : GridBase
    {
        public TradeManagementGrid(
            ServicesContainer servicesContainer,
            UserInterfaceEvents userInterfaceEvents
        ) : base("Trade Management", servicesContainer, userInterfaceEvents)
        {
        }

        public override void InitializeInitialToggleState()
        {
            initialToggleState = new Dictionary<string, bool>
            {
                { ButtonName.ENABLED, true },
                { ButtonName.AUTO, false }
            };
        }

        protected override void AddButtons()
        {
            var buttonModels = new List<ButtonModel>
            {
                new ButtonModel
                {
                    Name = ButtonName.ENABLED,
                    Content = "Disabled",
                    ToggledContent = "Enabled",
                    BackgroundColor = CustomColors.BUTTON_GREEN_COLOR,
                    HoverBackgroundColor = CustomColors.BUTTON_YELLOW_COLOR,
                    ToggledBackgroundColor = CustomColors.BUTTON_RED_COLOR,
                    TextColor = CustomColors.TEXT_COLOR,
                    ClickHandler = (Action<object, EventArgs>)HandleButtonClick,
                    IsToggleable = true,
                    InitialToggleState = initialToggleState[ButtonName.ENABLED]
                },
                new ButtonModel
                {
                    Name = ButtonName.AUTO,
                    Content = "Auto Disabled",
                    ToggledContent = "Auto Enabled",
                    BackgroundColor = CustomColors.BUTTON_GREEN_COLOR,
                    HoverBackgroundColor = CustomColors.BUTTON_HOVER_BG_COLOR,
                    ToggledBackgroundColor = CustomColors.BUTTON_BG_COLOR,
                    TextColor = CustomColors.TEXT_COLOR,
                    ClickHandler = (Action<object, EventArgs>)HandleButtonClick,
                    IsToggleable = true,
                    InitialToggleState = initialToggleState[ButtonName.AUTO]
                }
            };

            for (int i = 0; i < buttonModels.Count; i++)
            {
                var config = buttonModels[i];
                var button = new CustomButton(config).Button;
                buttons[config.Name] = button;

                // +1 because row 0 is the heading
                int row = (i / 2) + 1;
                int column = i % 2;

                AddButtonToGrid(button, row, column);
            }
        }

        public override void HandleButtonClick(object sender, EventArgs e)
        {
            Button button = (Button)sender;
            ButtonState state = (ButtonState)button.Tag;
            string buttonName = state.Config.Name;

            switch (buttonName)
            {
                case ButtonName.ENABLED:
                    userInterfaceEvents.EnabledDisabledTriggered(state.IsToggled);
                    break;

                case ButtonName.AUTO:
                    userInterfaceEvents.AutoTradeTriggered(state.IsToggled);
                    break;

                default:
                    throw new ArgumentException("Unknown button tag: " + buttonName);
            }
        }

        public override void HandleAutoTradeTriggered(bool isEnabled)
        {
            // No additional actions needed for Auto trade
        }
    }
}

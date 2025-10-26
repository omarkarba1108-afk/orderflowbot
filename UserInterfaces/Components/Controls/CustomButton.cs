using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Models;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components.Controls
{
    public class ButtonState
    {
        public bool IsToggled { get; set; }
        public ButtonModel Config { get; set; }
    }

    public class CustomButton
    {
        public Button Button { get; private set; }

        public CustomButton(ButtonModel config)
        {
            Button = CreateButton(config);
        }

        private Button CreateButton(ButtonModel config)
        {
            var button = new Button();

            // IMPORTANT: WPF Name must be a valid identifier (letters/digits/_ and not start with a digit)
            button.Name = SanitizeName(config.Name);

            button.Content = (config.IsToggleable && config.InitialToggleState) ? config.ToggledContent : config.Content;
            button.FontSize = 14;
            button.Visibility = Visibility.Visible;
            button.Foreground = UserInterfaceUtils.GetSolidColorBrushFromHex(config.TextColor);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Tag = new ButtonState { IsToggled = (config.IsToggleable && config.InitialToggleState), Config = config };
            button.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(config.BackgroundColor);
            button.BorderBrush = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(8);
            button.Margin = new Thickness(3);

            button.Style = CreateCustomButtonStyle(button);

            if (config.ClickHandler != null)
            {
                button.Click += (sender, e) =>
                {
                    if (config.IsToggleable)
                        ToggleButton(button);

                    // We only support sync handlers in NinjaScript
                    var syncHandler = config.ClickHandler as Action<object, RoutedEventArgs>;
                    if (syncHandler != null)
                        syncHandler(sender, e);
                };
            }

            button.IsEnabledChanged += (sender, e) => UpdateButtonState(button, button.IsEnabled);
            button.MouseEnter += (sender, e) =>
            {
                var state = (ButtonState)button.Tag;
                button.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(state.Config.HoverBackgroundColor);
            };
            button.MouseLeave += (sender, e) => UpdateButtonState(button, button.IsEnabled);

            // Start disabled style until grid enables
            UpdateButtonState(button, false);

            return button;
        }

        private Style CreateCustomButtonStyle(Button button)
        {
            var style = new Style(typeof(Button));

            // Template
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateButtonTemplate()));

            var state = (ButtonState)button.Tag;
            var cfg = state.Config;

            // Hover
            var hoverTrig = new Trigger();
            hoverTrig.Property = UIElement.IsMouseOverProperty;
            hoverTrig.Value = true;
            hoverTrig.Setters.Add(new Setter(Control.BackgroundProperty,
                UserInterfaceUtils.GetSolidColorBrushFromHex(cfg.HoverBackgroundColor)));
            style.Triggers.Add(hoverTrig);

            // Pressed
            var pressedTrig = new Trigger();
            pressedTrig.Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty;
            pressedTrig.Value = true;
            pressedTrig.Setters.Add(new Setter(Control.BackgroundProperty,
                UserInterfaceUtils.GetSolidColorBrushFromHex(cfg.HoverBackgroundColor)));
            style.Triggers.Add(pressedTrig);

            // Disabled
            var disabledTrig = new Trigger();
            disabledTrig.Property = UIElement.IsEnabledProperty;
            disabledTrig.Value = false;
            disabledTrig.Setters.Add(new Setter(Control.BackgroundProperty,
                UserInterfaceUtils.GetSolidColorBrushFromHex(CustomColors.BUTTON_DISABLED_BG_COLOR)));
            disabledTrig.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            style.Triggers.Add(disabledTrig);

            return style;
        }

        private ControlTemplate CreateButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(contentPresenter);

            template.VisualTree = border;
            return template;
        }

        private static void UpdateButtonState(Button button, bool isEnabled)
        {
            var state = (ButtonState)button.Tag;
            var config = state.Config;

            if (isEnabled)
            {
                if (config.IsToggleable)
                {
                    button.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(
                        state.IsToggled ? config.BackgroundColor : config.ToggledBackgroundColor);
                    button.Content = state.IsToggled ? config.ToggledContent : config.Content;
                }
                else
                {
                    button.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(config.BackgroundColor);
                    button.Content = config.Content;
                }
                button.Opacity = 1.0;
            }
            else
            {
                button.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(CustomColors.BUTTON_DISABLED_BG_COLOR);
                button.Opacity = 0.5;
            }

            button.IsEnabled = isEnabled;
        }

        private static void ToggleButton(Button button)
        {
            var state = (ButtonState)button.Tag;
            state.IsToggled = !state.IsToggled;
            UpdateButtonState(button, button.IsEnabled);
        }

        // ---- NEW: make WPF-safe names ----
        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "Btn";

            var sb = new System.Text.StringBuilder(s.Length + 1);

            // first char must be letter or underscore
            if (!(char.IsLetter(s[0]) || s[0] == '_'))
                sb.Append('B');

            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            return sb.ToString();
        }
    }
}

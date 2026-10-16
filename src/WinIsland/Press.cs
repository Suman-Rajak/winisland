using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace WinIsland;

/// <summary>
/// Makes a button feel pressable: it grows a touch under the pointer, sinks when pressed, and springs
/// back as it's let go. Set on the island's button styles (<c>local:Press.Effect</c> in App.xaml), so
/// every island button has it, including the ones in list rows.
/// </summary>
public static class Press
{
    private const float Hovered = 1.07f;
    private const float Pressed = 0.9f;

    public static readonly DependencyProperty EffectProperty = DependencyProperty.RegisterAttached(
        "Effect", typeof(bool), typeof(Press), new PropertyMetadata(false, OnEffectChanged));

    public static bool GetEffect(DependencyObject element) => (bool)element.GetValue(EffectProperty);

    public static void SetEffect(DependencyObject element, bool value) => element.SetValue(EffectProperty, value);

    // Buttons handle the pointer themselves, so these listen to handled events too.
    private static readonly PointerEventHandler s_entered = (sender, _) => To((UIElement)sender, Hovered);
    private static readonly PointerEventHandler s_exited = (sender, _) => To((UIElement)sender, 1);
    private static readonly PointerEventHandler s_pressed = (sender, _) => To((UIElement)sender, Pressed);
    private static readonly PointerEventHandler s_released = (sender, _) => To((UIElement)sender, Hovered);

    private static void OnEffectChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.AddHandler(UIElement.PointerEnteredEvent, s_entered, handledEventsToo: true);
            element.AddHandler(UIElement.PointerExitedEvent, s_exited, handledEventsToo: true);
            element.AddHandler(UIElement.PointerCanceledEvent, s_exited, handledEventsToo: true);
            element.AddHandler(UIElement.PointerPressedEvent, s_pressed, handledEventsToo: true);
            element.AddHandler(UIElement.PointerReleasedEvent, s_released, handledEventsToo: true);
        }
        else
        {
            element.RemoveHandler(UIElement.PointerEnteredEvent, s_entered);
            element.RemoveHandler(UIElement.PointerExitedEvent, s_exited);
            element.RemoveHandler(UIElement.PointerCanceledEvent, s_exited);
            element.RemoveHandler(UIElement.PointerPressedEvent, s_pressed);
            element.RemoveHandler(UIElement.PointerReleasedEvent, s_released);
        }
    }

    /// <summary>Springs the element to a size: briskly down when pressed, with a little bounce back up.</summary>
    private static void To(UIElement element, float scale)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = Motion.Centre(element);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(scale, scale, 1);
        spring.DampingRatio = scale < 1 ? 0.9f : 0.55f;
        spring.Period = TimeSpan.FromMilliseconds(scale < 1 ? 28 : 42);
        visual.StartAnimation(nameof(visual.Scale), spring);
    }
}

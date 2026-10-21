using System;
using System.Reflection;
using System.Windows.Forms;

namespace ExplorerNative
{
    /// <summary>
    /// A CheckBox that tells NVDA when it is ticked or unticked (Conner, 4.1.6:
    /// Space on a box said nothing, so he had to Tab away and back to hear it).
    ///
    /// Measured with a UIA and WinEvent watcher on .NET 10.0.13: a stock CheckBox
    /// raises no ToggleState property change and no MSAA state change when it is
    /// toggled, by Space, by UIA or by the mouse; only an empty Name change. NVDA
    /// reads WinForms through UIA since .NET 9, so it had nothing to say. This
    /// raises the ToggleState change through WinForms' own provider, whose method
    /// is internal and found once by reflection: the public UiaRaise call returned
    /// S_OK and reached no client, and AccessibilityNotifyClients raised nothing.
    /// If WinForms ever renames it, this box quietly behaves like a stock one.
    ///
    /// Use it for every check box. If a later WinForms raises the event itself,
    /// each toggle would be said twice: check again after a .NET upgrade.
    /// </summary>
    internal class SpokenCheckBox : CheckBox
    {
        private const int UIA_ToggleToggleStatePropertyId = 30086;

        private static readonly Func<AccessibleObject, int, int, bool>? RaiseToggle = FindRaise();
        private static readonly PropertyInfo? AccessibilityObjectCreated =
            typeof(Control).GetProperty("IsAccessibilityObjectCreated", BindingFlags.Instance | BindingFlags.NonPublic);

        private CheckState _announced;

        private static Func<AccessibleObject, int, int, bool>? FindRaise()
        {
            try
            {
                MethodInfo? raise = typeof(AccessibleObject).GetMethod("RaiseAutomationPropertyChangedEvent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                ParameterInfo[]? p = raise?.GetParameters();
                if (raise is null || p is null || p.Length != 3 || !p[0].ParameterType.IsEnum
                    || p[1].ParameterType != p[2].ParameterType || raise.ReturnType != typeof(bool))
                    return null;
                Type variant = p[1].ParameterType;
                MethodInfo? fromInt = null;
                foreach (MethodInfo m in variant.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "op_Explicit" && m.ReturnType == variant
                        && m.GetParameters() is [{ ParameterType: var t }] && t == typeof(int))
                    { fromInt = m; break; }
                }
                if (fromInt is null) return null;
                object property = Enum.ToObject(p[0].ParameterType, UIA_ToggleToggleStatePropertyId);
                return (target, oldValue, newValue) => (bool)raise.Invoke(target, new[]
                {
                    property,
                    fromInt.Invoke(null, new object[] { oldValue }),
                    fromInt.Invoke(null, new object[] { newValue }),
                })!;
            }
            catch (Exception) { return null; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _announced = CheckState;
        }

        protected override void OnCheckStateChanged(EventArgs e)
        {
            base.OnCheckStateChanged(e);
            CheckState now = CheckState;
            CheckState was = _announced;
            _announced = now;
            if (was == now || !IsHandleCreated || RaiseToggle is null) return;
            // Only once something has asked for the accessible object (a screen reader on focus).
            if (AccessibilityObjectCreated?.GetValue(this) is false) return;
            try
            {
                // ToggleState's values (Off 0, On 1, Indeterminate 2) are CheckState's.
                RaiseToggle(AccessibilityObject, (int)was, (int)now);
            }
            catch (Exception) { }   // a screen-reader courtesy never breaks a click
        }
    }
}

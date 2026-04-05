using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace COIJointVentures.UI;

// shared helpers for UIElements panels
internal static class UIHelpers
{
    public static void SetBorderRadius(VisualElement el, float r)
    {
        el.style.borderTopLeftRadius = r;
        el.style.borderTopRightRadius = r;
        el.style.borderBottomLeftRadius = r;
        el.style.borderBottomRightRadius = r;
    }

    public static void SetBorder(VisualElement el, float w, Color c)
    {
        el.style.borderTopWidth = w;
        el.style.borderBottomWidth = w;
        el.style.borderLeftWidth = w;
        el.style.borderRightWidth = w;
        el.style.borderTopColor = c;
        el.style.borderBottomColor = c;
        el.style.borderLeftColor = c;
        el.style.borderRightColor = c;
    }

    public static Button MakeButton(string text, Action onClick, Color bg)
    {
        var btn = new Button(onClick) { text = text };
        btn.style.backgroundColor = bg;
        btn.style.color = Color.white;
        btn.style.borderTopWidth = 0;
        btn.style.borderBottomWidth = 0;
        btn.style.borderLeftWidth = 0;
        btn.style.borderRightWidth = 0;
        btn.style.paddingTop = 0;
        btn.style.paddingBottom = 0;
        btn.style.unityTextAlign = TextAnchor.MiddleCenter;
        btn.style.alignItems = Align.Center;
        btn.style.justifyContent = Justify.Center;
        SetBorderRadius(btn, 4);
        btn.style.unityFontStyleAndWeight = FontStyle.Bold;
        btn.style.fontSize = 13;
        AddHoverEffect(btn);
        return btn;
    }

    public static VisualElement MakeTitleBar(string title, Action? onClose = null)
    {
        var bar = new VisualElement();
        bar.style.flexDirection = FlexDirection.Row;
        bar.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        bar.style.paddingLeft = 12;
        bar.style.paddingRight = 8;
        bar.style.paddingTop = 6;
        bar.style.paddingBottom = 6;
        bar.style.alignItems = Align.Center;
        bar.style.borderTopLeftRadius = 8;
        bar.style.borderTopRightRadius = 8;

        var lbl = new Label(title);
        lbl.style.fontSize = 14;
        lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
        lbl.style.color = Color.white;
        lbl.style.flexGrow = 1;
        bar.Add(lbl);

        if (onClose != null)
        {
            var closeBtn = MakeButton("X", onClose, new Color(0.6f, 0.2f, 0.2f));
            closeBtn.style.width = 26;
            closeBtn.style.height = 26;
            bar.Add(closeBtn);
        }

        return bar;
    }

    public static void MakeDraggable(VisualElement handle, VisualElement target)
    {
        bool dragging = false;
        Vector2 offset = default;

        handle.RegisterCallback<PointerDownEvent>(evt =>
        {
            dragging = true;
            offset = new Vector2(evt.position.x - target.resolvedStyle.left, evt.position.y - target.resolvedStyle.top);
            handle.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        });
        handle.RegisterCallback<PointerMoveEvent>(evt =>
        {
            if (!dragging) return;
            target.style.left = evt.position.x - offset.x;
            target.style.top = evt.position.y - offset.y;
            evt.StopPropagation();
        });
        handle.RegisterCallback<PointerUpEvent>(evt =>
        {
            dragging = false;
            handle.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        });
    }

    public static void StyleTextField(TextField field)
    {
        field.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
        field.style.color = Color.white;
        SetBorder(field, 1, new Color(0.4f, 0.4f, 0.45f));
        SetBorderRadius(field, 4);
        field.style.paddingLeft = 6;
        field.style.paddingRight = 6;
        field.style.paddingTop = 4;
        field.style.paddingBottom = 4;
        field.style.fontSize = 13;

        // the inner input element also needs styling
        var input = field.Q("unity-text-input");
        if (input != null)
        {
            input.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 1f);
            input.style.color = Color.white;
            input.style.borderTopWidth = 0;
            input.style.borderBottomWidth = 0;
            input.style.borderLeftWidth = 0;
            input.style.borderRightWidth = 0;
        }
    }

    public static void AddHoverEffect(VisualElement el)
    {
        el.RegisterCallback<MouseEnterEvent>(evt =>
        {
            if (!el.enabledSelf) return;
            var bg = el.resolvedStyle.backgroundColor;
            el.userData = bg; // stash the original color
            el.style.backgroundColor = new Color(
                Mathf.Min(bg.r + 0.08f, 1f),
                Mathf.Min(bg.g + 0.08f, 1f),
                Mathf.Min(bg.b + 0.08f, 1f),
                bg.a);
        });

        el.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            if (el.userData is Color orig)
                el.style.backgroundColor = orig;
        });
    }

    public static TextField MakeLabeledField(VisualElement parent, string label, string defaultValue, float labelWidth = 90)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.marginBottom = 6;

        var lbl = new Label(label);
        lbl.style.color = Color.white;
        lbl.style.width = labelWidth;
        lbl.style.fontSize = 13;
        row.Add(lbl);

        var field = new TextField();
        field.value = defaultValue;
        field.style.flexGrow = 1;
        StyleTextField(field);
        row.Add(field);

        parent.Add(row);
        return field;
    }
}

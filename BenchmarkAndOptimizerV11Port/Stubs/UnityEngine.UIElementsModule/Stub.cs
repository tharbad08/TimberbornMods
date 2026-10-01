using System;

namespace UnityEngine.UIElements
{
    public enum ScrollViewMode
    {
        Vertical,
        Horizontal,
        VerticalAndHorizontal
    }

    public enum ScrollerVisibility
    {
        Auto,
        AlwaysVisible,
        Hidden
    }
    public class EventBase { }
    public class ChangeEvent<T> : EventBase
    {
        public T newValue { get; set; } = default!;
    }

    public delegate void EventCallback<in TEventType>(TEventType evt) where TEventType : EventBase;

    public class VisualElement
    {
        public void Add(VisualElement child) { }
        public void Clear() { }
        public void RegisterCallback<TEventType>(EventCallback<TEventType> callback) where TEventType : EventBase { }
    }

    public static class UQueryExtensions
    {
        public static VisualElement Q(this VisualElement element, string name = null, string className = null) => null;
        public static T Q<T>(this VisualElement element, string name = null, string className = null) where T : VisualElement => null;
    }

    public class Label : VisualElement
    {
        public Label() { }
        public Label(string text) { this.text = text; }
        public string text { get; set; } = "";
    }

    public class Toggle : VisualElement
    {
        public Toggle() { }
        public string text { get; set; } = "";
        public bool value { get; set; }
        public void SetValueWithoutNotify(bool value) { this.value = value; }
    }

    public class ScrollView : VisualElement
    {
        public ScrollViewMode mode { get; set; }
        public ScrollerVisibility horizontalScrollerVisibility { get; set; }
        public ScrollerVisibility verticalScrollerVisibility { get; set; }
    }

    public class TextField : VisualElement
    {
        public string value { get; set; } = "";
    }

    public class Button : VisualElement
    {
        public string text { get; set; } = "";
        public event Action? clicked;
    }

    public class SliderInt : VisualElement
    {
        public int lowValue { get; set; }
        public int highValue { get; set; }
        public int value { get; set; }
        public void SetValueWithoutNotify(int value) { this.value = value; }
    }
}

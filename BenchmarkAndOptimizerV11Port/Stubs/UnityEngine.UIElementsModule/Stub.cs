using System;

namespace UnityEngine.UIElements
{
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

    public class ScrollView : VisualElement { }

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

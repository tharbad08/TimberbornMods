using System;

namespace UnityEngine.UIElements
{
    public class ChangeEvent<T>
    {
        public T newValue { get; set; } = default!;
    }

    public class VisualElement
    {
        public void Add(VisualElement child) { }
        public void Clear() { }
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
        public Toggle(string text) { this.text = text; }
        public string text { get; set; } = "";
        public bool value { get; set; }
        public void SetValueWithoutNotify(bool value) { this.value = value; }
        public void RegisterValueChangedCallback(Action<ChangeEvent<bool>> callback) { }
    }

    public class ScrollView : VisualElement { }

    public class TextField : VisualElement
    {
        public string value { get; set; } = "";
        public void RegisterValueChangedCallback(Action<ChangeEvent<string>> callback) { }
    }

    public class Button : VisualElement
    {
        public Button() { }
        public Button(Action clickEvent) { }
        public string text { get; set; } = "";
    }

    public class SliderInt : VisualElement
    {
        public SliderInt(int lowValue, int highValue) { }
        public int value { get; set; }
        public void SetValueWithoutNotify(int value) { this.value = value; }
        public void RegisterValueChangedCallback(Action<ChangeEvent<int>> callback) { }
    }
}

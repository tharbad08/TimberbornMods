using System;
namespace UnityEngine.UIElements
{
    public class VisualElement
    {
        public void Add(VisualElement child) { }
        public void Clear() { }
    }
    public class Label : VisualElement { public string text { get; set; } = ""; }
    public class Toggle : VisualElement
    {
        public string text { get; set; } = "";
        public bool value { get; set; }
        public void SetValueWithoutNotify(bool value) { this.value = value; }
    }
    public class ScrollView : VisualElement { }
    public class TextField : VisualElement { public string value { get; set; } = ""; }
    public class Button : VisualElement
    {
        public string text { get; set; } = "";
        public event Action? clicked;
    }
}

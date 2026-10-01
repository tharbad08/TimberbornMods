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
        public void SetValueWithoutNotify(bool value) { }
    }
    public class ScrollView : VisualElement { }
    public class Button : VisualElement
    {
        public string text { get; set; } = "";
        public event Action? clicked;
    }
}

using System; using System.Collections.Generic; using Bindito.Core;
namespace UiBuilder { public enum GameLabelSize { Normal, Big } public enum GameLabelColor { Default, Yellow } public enum ToggleStyle { Settings, GamePanel } public enum GameButtonSize { Medium, Small, Large } }
namespace TimberUi.CommonUi
{
    public readonly record struct SliderValues<T>(T Low,T High,T Default) where T:IComparable<T>;
    public class GameSliderInt : UnityEngine.UIElements.VisualElement
    {
        public int Value { get; set; }
        public GameSliderInt AddEndLabel(Func<int,string> f)=>this;
        public GameSliderInt RegisterChange(Action<int> a)=>this;
        public GameSliderInt SetValueWithoutNotify(int v){Value=v;return this;}
    }
    public class NineSliceTextField : UnityEngine.UIElements.TextField { }
    public class NineSliceButton : UnityEngine.UIElements.Button { }
}
namespace Bindito.Core
{
    public static class UiBuilderExtensions
    {
        public static Configurator BindSingleton<T>(this Configurator c) where T:class => c;
        public static Configurator MultiBindSingleton<T,TImpl>(this Configurator c) where T:class where TImpl:class,T => c;
    }
}
namespace UnityEngine.UIElements
{
    public static class UiBuilderExtensions
    {
        public static T SetPadding<T>(this T e,float padding) where T:VisualElement=>e;
        public static T SetMargin<T>(this T e,float top,float right,float bottom,float left) where T:VisualElement=>e;
        public static T SetWidthPercent<T>(this T e,float p) where T:VisualElement=>e;
        public static T SetMaxHeight<T>(this T e,float h) where T:VisualElement=>e;
        public static VisualElement AddRow(this VisualElement p)=>new VisualElement();
        public static Label AddGameLabel(this VisualElement p,string? text=null,string? name=null,IEnumerable<string>? additionalClasses=null,UiBuilder.GameLabelSize size=default,UiBuilder.GameLabelColor? color=default,bool bold=default,bool centered=default)=>new Label{ text=text??"" };
        public static Toggle AddToggle(this VisualElement p,string? text=null,string? name=null,IEnumerable<string>? additionalClasses=null,Action<bool>? onValueChanged=null,UiBuilder.ToggleStyle style=default)=>new Toggle{text=text??""};
        public static TimberUi.CommonUi.GameSliderInt AddSliderInt(this VisualElement p,string? label=null,string? name=null,IEnumerable<string>? additionalClasses=null,in TimberUi.CommonUi.SliderValues<int>? values=default)=>new TimberUi.CommonUi.GameSliderInt();
        public static TimberUi.CommonUi.NineSliceButton AddMenuButton(this VisualElement p,string? text=null,Action? onClick=null,string? name=null,IEnumerable<string>? additionalClasses=null,UiBuilder.GameButtonSize? size=default,bool stretched=false)=>new TimberUi.CommonUi.NineSliceButton{text=text??""};
        public static TimberUi.CommonUi.NineSliceTextField AddTextField(this VisualElement p,string? name=null,Action<string>? changeCallback=null,IEnumerable<string>? additionalClasses=null)=>new TimberUi.CommonUi.NineSliceTextField();
        public static ScrollView AddScrollView(this VisualElement p,string? name=null,IEnumerable<string>? additionalClasses=null,bool greenDecorated=true)=>new ScrollView();
    }
}

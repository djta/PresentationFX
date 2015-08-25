using System;
using Windows.Foundation.Metadata;
namespace Windows.UI.Xaml.Media.Animation
{
	[Activatable(100794368u), MarshalingBehavior(MarshalingType.Agile), Static(typeof(IPopupThemeTransitionStatics), 100794368u), Threading(ThreadingModel.Both), Version(100794368u), WebHostHidden]
	public sealed class PopupThemeTransition : Transition, IPopupThemeTransition
	{
		public extern double FromHorizontalOffset
		{
			get;
			set;
		}
		public extern double FromVerticalOffset
		{
			get;
			set;
		}
		public static extern DependencyProperty FromHorizontalOffsetProperty
		{
			get;
		}
		public static extern DependencyProperty FromVerticalOffsetProperty
		{
			get;
		}
		public extern PopupThemeTransition();
	}
}

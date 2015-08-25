using System;
using System.Runtime.InteropServices;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
namespace Windows.UI.Xaml.Media
{
	[Activatable(100794368u), MarshalingBehavior(MarshalingType.Agile), Threading(ThreadingModel.Both), Version(100794368u), WebHostHidden]
	public sealed class PathFigureCollection : IVector<PathFigure>, IIterable<PathFigure>
	{
		public extern uint Size
		{
			get;
		}
		public extern PathFigureCollection();
		public extern PathFigure GetAt([In] uint index);
		public extern IVectorView<PathFigure> GetView();
		public extern bool IndexOf([In] PathFigure value, out uint index);
		public extern void SetAt([In] uint index, [In] PathFigure value);
		public extern void InsertAt([In] uint index, [In] PathFigure value);
		public extern void RemoveAt([In] uint index);
		public extern void Append([In] PathFigure value);
		public extern void RemoveAtEnd();
		public extern void Clear();
		public extern uint GetMany([In] uint startIndex, [Out] PathFigure[] items);
		public extern void ReplaceAll([In] PathFigure[] items);
		public extern IIterator<PathFigure> First();
	}
}

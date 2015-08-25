using System;
using Windows.Foundation.Metadata;
namespace Windows.UI.Text
{
	[Version(100794368u)]
	public enum LinkType
	{
		Undefined,
		NotALink,
		ClientLink,
		FriendlyLinkName,
		FriendlyLinkAddress,
		AutoLink,
		AutoLinkEmail,
		AutoLinkPhone,
		AutoLinkPath
	}
}

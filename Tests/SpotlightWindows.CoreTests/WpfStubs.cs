// Stand-in for the single WPF type that Core/Models/SearchResult.cs references,
// so the Core logic can be compiled and tested without the WPF assemblies.
namespace System.Windows.Media
{
    public abstract class ImageSource { }
}

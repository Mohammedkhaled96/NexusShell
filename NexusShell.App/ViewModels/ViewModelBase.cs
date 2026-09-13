using CommunityToolkit.Mvvm.ComponentModel;

namespace NexusShell.App.ViewModels
{
    /// <summary>
    /// Base for all VMs. Inherits from CommunityToolkit.Mvvm's
    /// <see cref="ObservableObject"/> which exposes the same
    /// <c>SetProperty</c> / <c>OnPropertyChanged</c> surface our VMs already
    /// use — so existing code compiles unchanged — while also unlocking the
    /// <c>[ObservableProperty]</c> and <c>[RelayCommand]</c> source generators
    /// for any class declared <c>partial</c>.
    ///
    /// Net benefit: faster <c>INotifyPropertyChanged</c> dispatch (cached
    /// <c>PropertyChangedEventArgs</c>), and an opt-in upgrade path per VM
    /// without a flag-day rewrite.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
    }
}

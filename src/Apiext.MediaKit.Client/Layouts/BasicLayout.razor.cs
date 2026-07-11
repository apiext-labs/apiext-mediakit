using AntDesign.ProLayout;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;

namespace Apiext.MediaKit.Layouts
{
    public partial class BasicLayout : LayoutComponentBase
    {
        private MenuDataItem[] _menuData = [];

        [Inject] private NavigationManager Navigation { get; set; } = default!;
        [Inject] private HttpClient HttpClient { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            _menuData = await LoadMenuAsync("data/menu.json") ?? [];
        }

        private async Task<MenuDataItem[]?> LoadMenuAsync(string url)
        {
            try
            {
                using var response = await HttpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadFromJsonAsync<MenuDataItem[]>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
    }
}

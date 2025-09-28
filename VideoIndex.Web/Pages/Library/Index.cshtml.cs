using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VideoIndex.Web.Pages.Library
{
    public class IndexModel : PageModel
    {
        public void OnGet()
        {
            // No server work here; the page fetches /api/media/browse client-side.
        }
    }
}

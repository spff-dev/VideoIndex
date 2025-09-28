using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VideoIndex.Core.Data;
using VideoIndex.Core.Models;

namespace VideoIndex.Web.Pages.Scan
{
    public class IndexModel : PageModel
    {
        private readonly IDbContextFactory<VideoIndexDbContext> _factory;
        public List<ScanRoot> Roots { get; private set; } = new();

        public IndexModel(IDbContextFactory<VideoIndexDbContext> factory) => _factory = factory;

        public async Task OnGetAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            Roots = await db.ScanRoots.OrderBy(r => r.Name).ToListAsync();
        }
    }
}

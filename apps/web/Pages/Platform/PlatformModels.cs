using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Proj37.CostEstimator.Web.Pages.Platform;

// Lightweight page models for the Intelligence Platform sub-pages. Rendering is client-side
// (app.js reads the current estimation from the API / local store), so these just back the routes.

public sealed class ScopeModel : PageModel { }
public sealed class RequirementsModel : PageModel { }
public sealed class CostModel : PageModel { }
public sealed class ProjectCostModel : PageModel { }
public sealed class OperationsModel : PageModel { }
public sealed class StepsModel : PageModel { }

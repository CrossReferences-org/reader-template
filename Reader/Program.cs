using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Http.HttpResults;
using Reader.Components;
using Reader.Components.Reading;
using Reader.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

// Write Afrikaans, French and typographic characters as they are, not as &#x...; entities.
// Registered after AddRazorComponents so this is the encoder the renderer resolves.
builder.Services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));
builder.Services.AddSingleton(sp => CorpusLoader.Load(
    Path.Combine(AppContext.BaseDirectory, "json"),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Corpus")));

var app = builder.Build();

// Load the data before accepting requests, so the first reader doesn't wait for it.
var corpus = app.Services.GetRequiredService<Corpus>();
var defaultTranslation = corpus.Find(app.Configuration["Reader:DefaultTranslation"]) ?? corpus.Views[0];

// Hosting under a sub-path (e.g. behind nginx at /reader) only needs Reader:PathBase set.
var pathBase = app.Configuration["Reader:PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();

// The root returns a reader to where they last were, or to the start.
app.MapGet("/", (HttpContext http) =>
{
    var target = $"1/1/{defaultTranslation.T.Code}";
    var parts = http.Request.Cookies["cr-last"]?.Split('/');
    if (parts is [var b, var c, var t]
        && int.TryParse(b, out var book) && int.TryParse(c, out var chapter)
        && corpus.Find(t) is { } view && view.Chapter(book, chapter) is not null)
    {
        target = $"{book}/{chapter}/{view.T.Code}";
    }
    return Results.Redirect($"{http.Request.PathBase}/{target}");
});

// Fragments for the reference pane. The data is frozen while the app runs, so browsers may
// keep them for a day; a data update shows up without anyone clearing a cache.
var pane = app.MapGroup("/pane/{tr}");

pane.MapGet("/anchors", Results<RazorComponentResult<PaneFragment>, NotFound> (string tr, string? k, HttpContext http) =>
{
    if (corpus.Find(tr) is not { } view || string.IsNullOrEmpty(k)) return TypedResults.NotFound();
    var model = PaneBuilder.ForAnchors(corpus, view, k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return model is null ? TypedResults.NotFound() : Fragment(http, model);
});

pane.MapGet("/verse/{book:int}/{chapter:int}/{verse:int}", Results<RazorComponentResult<PaneFragment>, NotFound> (string tr, int book, int chapter, int verse, HttpContext http) =>
{
    if (corpus.Find(tr) is not { } view) return TypedResults.NotFound();
    var model = PaneBuilder.ForVerse(view, book, chapter, verse);
    return model is null ? TypedResults.NotFound() : Fragment(http, model);
});

// A citing verse from the cited-by list, opened in place.
pane.MapGet("/preview", Results<RazorComponentResult<PreviewFragment>, NotFound> (string tr, string? k, HttpContext http) =>
{
    if (corpus.Find(tr) is not { } view || string.IsNullOrEmpty(k)) return TypedResults.NotFound();
    var model = PaneBuilder.ForPreview(corpus, view, k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (model is null) return TypedResults.NotFound();
    http.Response.Headers.CacheControl = "public, max-age=86400";
    return new RazorComponentResult<PreviewFragment>(new { Model = model });
});

app.MapRazorComponents<App>();

app.Run();

static RazorComponentResult<PaneFragment> Fragment(HttpContext http, PaneModel model)
{
    http.Response.Headers.CacheControl = "public, max-age=86400";
    return new RazorComponentResult<PaneFragment>(new { Model = model });
}

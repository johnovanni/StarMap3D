Nearby stars - WPF (.NET 8, Windows)

Build & run:   dotnet run -c Release      (or open StarMap3D.csproj in Visual Studio 2022)

On first start the app downloads (needs internet, once) and caches in %LocalAppData%\StarMap3D:
  - HYG star catalogue (GitHub)              -> every catalogued star within 300 ly
  - NASA Exoplanet Archive (TAP, pscomppars) -> planet-hosting systems within 300 ly
Use "Refresh data" to re-download. With no internet and no cache, ~40 well-known stars are shown
instead (that fallback list only goes out to about 51 ly).

The "Max distance" slider in the side panel (5-300 ly, default 100 ly) controls how far out the
map displays. Catalogues are always downloaded out to 300 ly, so moving the slider just re-filters
data already in memory - no re-download, no network access.

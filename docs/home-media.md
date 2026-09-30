# Home recent media and decorative backgrounds

The home bridge reads at most 20 films and 20 series from the active provider's local index. Each query filters its catalog before applying the limit and sorts by provider date descending, then title and remote ID. A missing date excludes the item. Empty sections are hidden. Both carousels reuse the existing detail dialog and its playback/download actions.

## Inspected provider fields

A limited read-only sample of the configured Xtream provider showed:

- Film list: `stream_icon`, `added`.
- Film detail: `info.movie_image`, `info.backdrop`, and an empty `info.backdrop_path` array in the sampled response.
- Series list and detail: `cover`, `backdrop_path` arrays, `last_modified`.

These are observations of a small sample, not guarantees for every provider or item. The implementation accepts `backdrop_path` (array or string) and `backdrop` when they contain an eligible absolute image URL. It does not invent `cover_big`, `fanart`, `background` or other unobserved sources.

There was no configured Stalker provider available for a live sample. Inspection of the adapter and fixtures shows `screenshot_uri`, `cover` and `logo` as poster sources. Stalker home novelty sections remain disabled because dates/list ordering have not been established as reliable.

## Dates and artwork selection

Films use valid `added`, `added_at` or `created_at` values. Xtream series also accept `last_modified` when no valid addition date is supplied. This represents provider recency, including updates to an existing series, not a claim about its original creation date. The series heading tooltip explains this. Release years and the local rebuild time never become novelty dates.

The normal rebuild captures backdrops already present in catalog lists. Neither the home nor reconstruction hydrates the whole series catalog: only the same dated top 20 series may receive targeted detail requests for missing/unusable posters (see below). A film backdrop available only in its detail endpoint is not fetched for decoration; its indexed poster is the fallback.

Background candidates start with the first continue-watching poster, then up to six recent films and six recent series. Per content, a safe backdrop precedes the safe poster. Invalid, failed or timed-out images are skipped; failure of a backdrop tries its poster. With no usable image, the neutral background remains intact. No external metadata API is added.

## Storage and URL policy

Database migration 4 marks VOD/series index policies dirty once, in a transaction. It does not alter any open search index. The next normal atomic rebuild adds nullable `search_documents.backdrop_url` and a catalog/date/title/ID index. Old indexes remain readable with a null backdrop. `SearchIndexFileAccess`, scoped readers/connections, targeted `ClearPool`, and the existing atomic replacement sequence are preserved. `playback_history` and resume policy are unchanged.

Decorative backgrounds retain the unchanged `HomeArtwork.SafeUrl` policy: absolute HTTP(S) static image URLs only, no user-info, queries or fragments. Card/detail posters use `MediaArtwork.SafeImageUrl`, which also accepts public CDN transformation queries and extensionless image endpoints. Both reject credential markers (including encoded values), playback paths, MAC addresses and known provider secrets when supplied. Index construction, enrichment/cache writes and home responses validate posters with the provider secret; cached and legacy-index images are checked again before delivery. Unsafe artwork becomes null, never a rewritten URL. The background candidate selection still applies its stricter policy to poster fallbacks too. Image requests have no referrer; no provider secret crosses the home bridge.

## Targeted series poster recovery

`RecentSeriesArtwork` reads at most the 20 recent series, reuses saved posters, and requests `GetSeriesInfoAsync` only for missing or rejected images. `JsonSupport.Item` accepts the first nonempty `cover`/`movie_image` for series. Provider requests are limited to three simultaneously across home requests and rebuilds. Overlapping requests for the same provider/ID share the persisted result. The optional enrichment has an eight-second budget and respects caller cancellation; failure never fails the home response. The HTTP client's existing transient retry policy is unchanged.

When a syntactically safe indexed URL actually returns 404 or otherwise fails in the browser, the series card requests `home.seriesArtwork` once. The bridge verifies the active provider, membership in the current recent 20, and the failed URL against indexed/cached artwork. Only that poster holder is replaced; there is no carousel/layout reset. The replacement has a placeholder fallback but does not start another recovery loop. Navigation/provider changes cancel pending recovery requests and discard stale results.

`series_artwork` in the application database stores only provider key, series ID, a validated poster URL (or null), and check time. It is separate from replaceable search files and cascades on provider deletion. Positive results survive subsequent homes and index rebuilds; empty/error results impose a five-minute retry cooldown. A newly enriched image failing to load is not immediately refetched. There is no SQLite connection or index lease held during provider I/O, no new search-index writer, and no change to `SearchIndexFileAccess`/pool clearing/atomic replacement. Before installing a rebuilt index, only the same top 20 series are enriched and their available posters are copied into the new documents.

Local diagnostic on 2026-09-30: the two reported series both had indexed poster URLs accepted by the old detail and background filters, but the public image requests returned HTTP 404. `get_series` and both targeted `get_series_info` requests returned HTTP 520, so live list/detail equality or a replacement URL could not be established. This observation does not support assuming missing list covers or filter rejection for those two examples. No URLs or credentials were recorded. Fixture tests cover missing list artwork, CDN filtering, broken indexed posters, cache reuse/isolation, failures, cancellation, concurrency/20-item limits and atomic rebuilds.

A Browser/local-mock check recovered two deliberately broken series posters with two targeted requests. Both cards remained 156 × 286 px with `object-fit: cover`, their detail dialog still opened, and reopening home added no recovery requests. This validates the DOM update, not live provider/WebView2 recovery while the provider API is unavailable.

## Presentation and checks

Recent cards use a non-wrapping horizontal flex track with native overflow, proximity snapping, Shift+wheel support, focusable cards/track and previous/next buttons advancing four cards. Buttons disable at the ends. Width changes reveal fewer or more fixed-width cards without adding rows.

Two decorative DOM image layers occupy only the main grid column and ignore pointer events. The backdrop sits at stacking level 0, below the existing main/sidebar content at level 1. Images use cover, 7 px blur, brightness .56, saturation .90, active opacity .74 and scale 1.03 to make the background image more recognizable while retaining a soft, dark presentation. The horizontal dark gradient uses 58%/26%/42% opacity, with a separate bottom fade; this keeps the image perceptible without changing card backgrounds. A 15-second single timer starts a preloaded opacity crossfade lasting 1.8 seconds. Timers/preloads stop when leaving home or hiding the document. Reduced motion disables rotation and fades.

Backend regressions cover bounded sorting, provider/catalog isolation, undated data, provider parsing, safe/fallback artwork, legacy index replacement after pooled reads, and idempotent invalidation. A local browser preview with synthetic data was checked at 1920, 800 and 600 px: one row per carousel, four-card arrows, keyboard endpoints, image-failure fallback, main-only backdrop, and existing film/series dialogs with playback/download actions. This preview does not constitute end-to-end playback testing in WebView2.

The backdrop visibility follow-up was checked at 1920, 1366 and 800 px with only synthetic portrait posters: successful loading into both layers, a perceptible blurred background, the 1.8-second opacity crossfade, independent sidebar, no page-width overflow, and unobstructed card/quick-link clicks. No carousel, artwork filtering or rotation logic was changed.

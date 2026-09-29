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

The normal rebuild captures backdrops already present in catalog lists. The home does not fetch entire catalogs or fan out into detail requests. A film backdrop available only in its detail endpoint is therefore not fetched for decoration; its indexed poster is the fallback.

Background candidates start with the first continue-watching poster, then up to six recent films and six recent series. Per content, a safe backdrop precedes the safe poster. Invalid, failed or timed-out images are skipped; failure of a backdrop tries its poster. With no usable image, the neutral background remains intact. No external metadata API is added.

## Storage and URL policy

Database migration 4 marks VOD/series index policies dirty once, in a transaction. It does not alter any open search index. The next normal atomic rebuild adds nullable `search_documents.backdrop_url` and a catalog/date/title/ID index. Old indexes remain readable with a null backdrop. `SearchIndexFileAccess`, scoped readers/connections, targeted `ClearPool`, and the existing atomic replacement sequence are preserved. `playback_history` and resume policy are unchanged.

Only absolute HTTP(S) static image URLs are eligible for persisted artwork and the home response. User-info, all queries/fragments (including signed URLs), sensitive path markers, media playback paths, MAC addresses and the configured provider's credential values are rejected before indexing and before the home bridge returns images. Known credential checks also cover decoded paths. Unsafe artwork becomes null; it is not stripped into a different URL. Existing old index URLs are filtered again on home reads. Background image requests have no referrer. The provider's secret never crosses the bridge.

## Presentation and checks

Recent cards use a non-wrapping horizontal flex track with native overflow, proximity snapping, Shift+wheel support, focusable cards/track and previous/next buttons advancing four cards. Buttons disable at the ends. Width changes reveal fewer or more fixed-width cards without adding rows.

Two decorative DOM image layers occupy only the main grid column and ignore pointer events. Images use cover, 28 px blur, reduced brightness/saturation and dark gradients. A 15-second single timer starts a preloaded opacity crossfade lasting 1.8 seconds. Timers/preloads stop when leaving home or hiding the document. Reduced motion disables rotation and fades.

Backend regressions cover bounded sorting, provider/catalog isolation, undated data, provider parsing, safe/fallback artwork, legacy index replacement after pooled reads, and idempotent invalidation. A local browser preview with synthetic data was checked at 1920, 800 and 600 px: one row per carousel, four-card arrows, keyboard endpoints, image-failure fallback, main-only backdrop, and existing film/series dialogs with playback/download actions. This preview does not constitute end-to-end playback testing in WebView2.

#!/usr/bin/env python3
"""
WCAG 2.1 AA contrast verification for the Catppuccin Mocha palette
shipped in src/CopilotSessionManager/Themes/CatppuccinMocha.xaml.

Linearises every named brush, computes the contrast ratio against every
plausible pairing (text on every neutral surface, badge text on every
coloured pill, focus outlines + status pills as UI components on every
background), and prints any pair that falls below the WCAG 2.1 AA
threshold:

  * 4.5:1 for normal text
  * 3.0:1 for non-text UI components and focus indicators

Usage:

    python scripts/contrast-check.py

Exits 0 even when the two decorative-border pairs fail, since they are
exempt under WCAG 2.1 SC 1.4.11 (purely decorative). Other failures
print to stderr; CI treats any non-decorative failure as a regression.

History: introduced as part of #133 (V1.2) when the palette was retuned
to clear nine WCAG 2.1 AA shortfalls discovered during the V1.1 release
audit. Re-run after any change to CatppuccinMocha.xaml.
"""

import sys

# --- WCAG 2.1 helpers ------------------------------------------------------


def to_lin(c: float) -> float:
    """Convert a single sRGB component (0-255) to linear-light (0-1)."""
    c = c / 255.0
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4


def luminance(rgb):
    r, g, b = rgb
    return 0.2126 * to_lin(r) + 0.7152 * to_lin(g) + 0.0722 * to_lin(b)


def contrast(c1, c2) -> float:
    L1, L2 = luminance(c1), luminance(c2)
    if L1 < L2:
        L1, L2 = L2, L1
    return (L1 + 0.05) / (L2 + 0.05)


# --- Palette (must mirror Themes/CatppuccinMocha.xaml) --------------------

# Source-of-truth file: src/CopilotSessionManager/Themes/CatppuccinMocha.xaml
brushes = {
    "Background":      (30, 30, 46),
    "Surface":         (24, 24, 37),
    "SurfaceAlt":      (17, 17, 27),
    "SurfaceHover":    (37, 37, 55),
    "Overlay":         (49, 50, 68),
    "Border":          (69, 71, 90),
    "TextPrimary":     (205, 214, 244),
    "TextSecondary":   (166, 173, 200),
    "TextMuted":       (186, 194, 222),  # subtext0 (#133)
    "TextPlaceholder": (147, 153, 178),  # subtext1 (#133)
    "TextOnAccent":    (17, 17, 27),
    "BadgeText":       (17, 17, 27),
    "Accent":          (249, 226, 175),
    "FocusOutline":    (249, 226, 175),
    "PrimaryAction":   (137, 180, 250),
    "Link":            (137, 180, 250),
    "Success":         (166, 227, 161),
    "Danger":          (243, 139, 168),
    "WarningSurface":  (61, 44, 14),
    "DangerSurface":   (61, 31, 38),
    "StatusWorking":   (166, 227, 161),
    "StatusAwaiting":  (249, 226, 175),
    "StatusInput":     (137, 180, 250),
    "StatusIdle":      (127, 132, 156),
    "StatusInactive":  (147, 153, 178),  # subtext1 (#133)
    "StatusCrashed":   (243, 139, 168),
    "BadgeOpen":       (166, 227, 161),
    "BadgeClosed":     (203, 166, 247),
    "BadgeMerged":     (203, 166, 247),
    "BadgeDraft":      (127, 132, 156),
    "BadgeNeutral":    (147, 153, 178),  # subtext1 (#133)
    "BadgeFailure":    (243, 139, 168),
    "BadgePending":    (249, 226, 175),
    "LabelOrange":     (250, 179, 135),
    "LabelBlue":       (137, 180, 250),
    "LabelTeal":       (148, 226, 213),
    "LabelRed":        (243, 139, 168),
    "LabelPurple":     (203, 166, 247),
    "LabelCyan":       (137, 220, 235),
}

# --- Pair definitions (every plausible foreground/background combination) -

# Decorative-border pairs that fail by design and are exempt under
# WCAG 2.1 SC 1.4.11 ("Pure decoration … has no information or
# functionality"). Listed here so we can announce them as expected.
DECORATIVE_EXEMPT = {
    ("Border", "Background"),
    ("Border", "Surface"),
}

pairs = []

# 1. Body / muted / secondary text on every neutral surface -> 4.5
for bg in ["Background", "Surface", "SurfaceAlt", "SurfaceHover", "Overlay"]:
    for fg in ["TextPrimary", "TextSecondary", "TextMuted"]:
        pairs.append((fg, bg, 4.5, "text"))

# 2. Placeholder text on the input surfaces it actually lives on -> 4.5
for bg in ["Background", "Surface"]:
    pairs.append(("TextPlaceholder", bg, 4.5, "text"))

# 3. Body text on banner backgrounds -> 4.5
for bg in ["WarningSurface", "DangerSurface"]:
    pairs.append(("TextPrimary", bg, 4.5, "text"))

# 4. Dark badge text on every coloured pill -> 4.5
COLOURED_PILLS = [
    "StatusWorking", "StatusAwaiting", "StatusInput", "StatusIdle",
    "StatusInactive", "StatusCrashed",
    "BadgeOpen", "BadgeClosed", "BadgeMerged", "BadgeDraft",
    "BadgeNeutral", "BadgeFailure", "BadgePending",
    "LabelOrange", "LabelBlue", "LabelTeal", "LabelRed", "LabelPurple",
    "LabelCyan",
    "Accent", "Success", "Danger",
]
for bg in COLOURED_PILLS:
    pairs.append(("BadgeText", bg, 4.5, "text"))

# 5. Links on neutral backgrounds -> 4.5
for bg in ["Background", "Surface", "SurfaceAlt"]:
    pairs.append(("Link", bg, 4.5, "text"))

# 6. Focus outline (UI component) -> 3.0
for bg in ["Background", "Surface", "SurfaceAlt", "SurfaceHover"]:
    pairs.append(("FocusOutline", bg, 3.0, "ui"))

# 7. Borders (UI component) -> 3.0 (Background/Surface pairs are decorative
# per WCAG 1.4.11 — see DECORATIVE_EXEMPT above)
for bg in ["Background", "Surface"]:
    pairs.append(("Border", bg, 3.0, "ui"))

# 8. Status pill backgrounds against the row background (UI component) -> 3.0
for fg in ["StatusWorking", "StatusAwaiting", "StatusInput", "StatusIdle",
           "StatusInactive", "StatusCrashed"]:
    for bg in ["Background", "Surface"]:
        pairs.append((fg, bg, 3.0, "ui"))


# --- Run ------------------------------------------------------------------

passed, failed_real, failed_exempt = [], [], []
for fg, bg, thr, kind in pairs:
    r = contrast(brushes[fg], brushes[bg])
    rec = (fg, bg, thr, kind, r)
    if r >= thr:
        passed.append(rec)
    elif (fg, bg) in DECORATIVE_EXEMPT:
        failed_exempt.append(rec)
    else:
        failed_real.append(rec)

print(f"Pairs checked:                {len(pairs)}")
print(f"Passed:                       {len(passed)}")
print(f"Failed (decorative, exempt):  {len(failed_exempt)}")
print(f"Failed (real):                {len(failed_real)}")
print()

if failed_exempt:
    print("=== EXEMPT (decorative borders, WCAG 2.1 SC 1.4.11) ===")
    for fg, bg, thr, kind, r in sorted(failed_exempt, key=lambda x: x[4]):
        print(f"  [{kind:4s}]  {fg:18s} on {bg:16s}  {r:5.2f}:1  (informational)")
    print()

if failed_real:
    print("=== FAILURES (regressions) ===", file=sys.stderr)
    for fg, bg, thr, kind, r in sorted(failed_real, key=lambda x: x[4]):
        print(
            f"  [{kind:4s}]  {fg:18s} on {bg:16s}  {r:5.2f}:1  "
            f"(needs >= {thr})",
            file=sys.stderr,
        )
    sys.exit(1)

print("All non-decorative pairs meet WCAG 2.1 AA. ✓")

# PitLaunch Design System

## Intent

An enthusiast is switching the same PC between a bright desk and a dim sim rig. The interface therefore uses a dark neutral shell with strong type, restrained color, and short state transitions. It should feel native to Windows 11, not like a website placed in a desktop window.

## Structure

- Native frameless window, 1160 x 760 default and 940 x 620 minimum.
- A compact 216 px navigation rail holds the product identity and destinations.
- The content stage uses a 56 px outer gutter and a readable maximum content width.
- Setup switching uses full-width rows with a stable action column, not a loose card grid.
- Profile configuration uses a clear header, a two-option segmented tab, then unframed sections or single-purpose panels.
- Settings use grouped rows with controls aligned to one predictable right edge.

## Color

Restrained strategy. Charcoal neutrals carry the interface; mint appears only for primary actions, active state, and success. Blue, amber, and coral are reserved for semantic information, warnings, and errors.

| Role | Value | Use |
| --- | --- | --- |
| Window | `#0E100F` | Outer window and title bar |
| Navigation | `#121513` | Persistent navigation rail |
| Canvas | `#171A18` | Main content stage |
| Surface | `#1D211E` | Grouped controls and setup rows |
| Raised | `#252A26` | Menus, dialogs, hover |
| Border | `#343A35` | Dividers and control outlines |
| Text | `#F1F3EE` | Primary text, never pure white |
| Muted | `#A5ABA3` | Secondary text |
| Faint | `#737A72` | Tertiary text and disabled state |
| Accent | `#82DDBB` | Primary action and active state |
| Accent soft | `#18362D` | Selected backgrounds |
| Info | `#82AFFF` | Informational state |
| Warning | `#E7B86C` | Partial success and caution |
| Error | `#EF8E86` | Failure and destructive action |

## Typography

- Family: `Segoe UI Variable Text`, falling back to `Segoe UI`.
- Display and page title: 28 px semibold.
- Section title: 16 px semibold.
- Control and body: 13 px regular or semibold.
- Caption: 12 px regular.
- Metadata: 11 px regular.
- Letter spacing remains zero. Text is sentence case except compact status labels.

## Components

- Buttons are 36-40 px tall, 6 px radius, with default, hover, pressed, focused, disabled, and busy states.
- Icon buttons use Segoe Fluent Icons with tooltips and a 36 px target.
- Inputs and pickers are 40 px tall with one border and an accent focus ring.
- Toggles use a compact 38 x 22 track and always sit beside a written label.
- Setup rows are 84 px tall with a profile mark, name, captured summary, active status, and a fixed action area.
- Tabs are a compact segmented control; the selected tab has a raised background and stronger text.
- Toasts enter from the lower right and stay concise. Dialogs use a focused 440 px panel.

## Motion

- Page transitions: 160-200 ms fade and 10 px horizontal translation.
- Hover transitions: 120-160 ms color or border change only.
- Toasts: 180 ms fade and vertical translation.
- System switching: delay the busy overlay by 140 ms so fast no-op switches do not flash.
- No scaling cards, bouncing, elastic easing, or decorative page-load choreography.

## Content Rules

- Use `Setup` for a captured profile in user-facing copy.
- Use `Switch` for activating another setup and `Reapply` for the active one.
- Keep device IDs and implementation details out of the interface.
- Explain destructive and recapture actions before confirmation.

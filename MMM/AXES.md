# Axes of the MMM implementation

Space follows the author's ruling of 2026-10-15 (M3266, R1): MKS units; looking north, **x points east, y points north, z points up** (East-North-Up, a right-handed frame).

- `CartPosition` of a Position is `[x, y, z]` in that frame. A Location's place (`AddLocation(id, ..., x, y, ...)`) and where an Item stands within its Location are `x` east and `y` north; the height (`z`) is 0 here.
- The state the server gives the viewer (`/viewer/state`) has `X` (east), `Y` (north), `Z` (up) for every Location and Item.
- The viewer draws with three.js, whose axes are x east, y up, z towards the viewer: `x = X`, `y = Z`, `z = -Y` (north is away from the viewer). This conversion is the viewer's alone.
- The frame of an object (an avatar, a car) follows the logic of the object and is its own; the Orientation of the object relates the two.

**Orientation** (author's ruling, 2026-10-15): the three angles of an Orientation are **yaw, pitch and roll**, in degrees. In space, yaw is the rotation about the vertical axis (z, up); pitch and roll are the rotations about the object's own lateral and forward axes, which belong to the object's own frame. Still to be stated in the specification, as my reading and not the author's: the zero of yaw (east or north), the signs of the rotations, and the order in which they apply. All the Orientations of Use Case 2 are zero, so nothing depends on it yet.
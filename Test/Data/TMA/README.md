# Test data for OSD-TMA (Television Media Analysis)

`AMI-IS1009c-10m00-15m00-side-by-side.mp4`: four people seated at a table, talking among themselves; 5 minutes (10:00-15:00 of the meeting), 1280x720, 25 frames/s, H.264, with AAC audio.

- **Source:** AMI Meeting Corpus, meeting IS1009c (Idiap room), https://groups.inf.ed.ac.uk/ami/corpus/ - the left and right room cameras (`IS1009c.L_orig.avi`, `IS1009c.R_orig.avi`, 720x576), each showing two participants from the front, and the mix of the four headset microphones (`IS1009c.Mix-Headset.wav`).
- **Licence:** Creative Commons Attribution 4.0 International (CC BY 4.0). Credit: "AMI Meeting Corpus, meeting IS1009c, https://groups.inf.ed.ac.uk/ami/corpus/, CC BY 4.0".
- **Changes made (as CC BY 4.0 requires them to be indicated):** 10:00-15:00 cut out; the black borders of each camera picture removed (crop 704x566 from x=8, y=8); the two pictures scaled to 640x516 and joined side by side, left camera on the left; placed in a 1280x720 frame with black bands above and below; the headset mix added as the audio track, aligned with the pictures at the start (measured offset 0.2 s); made with ffmpeg 9.0.2 on 2026-11-14.
- **For TMA:** all four faces are visible all the time, two in each half of the picture; what TMA has to say is "A speaks, B speaks", not who the people are.
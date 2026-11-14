# TruckingAdvisor.EdgeTts

Free Microsoft neural voices for the CB radio (the voices Microsoft Edge's "Read aloud" uses).

This library is a C# port of parts of [edge-tts](https://github.com/rany2/edge-tts) 7.3.1 by rany2
(`drm.py` and the synthesis part of `communicate.py`) and is therefore licensed under the
**GNU Lesser General Public License v3.0 or later** (`LICENSE.LGPL`, which builds on `LICENSE.GPL`).
The rest of TruckingAdvisor is MIT-licensed and uses this library through its public API only;
you can replace `TruckingAdvisor.EdgeTts.dll` with a modified build of this source.

The service is provided by Microsoft for Edge and is not an official API: it needs an internet
connection and could change. TruckingAdvisor falls back to Windows voices when it isn't available.

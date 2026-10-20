# How USB Audio Player PRO's exclusive / bit-perfect output works, and how it maps to Windows

This write-up uses USB Audio Player PRO's (UAPP, by eXtream Software Development) public documentation, its
observable behaviour, and the USB Audio Class / Android audio specifications.
It is a clean-room analysis. The APK was **not** decompiled, and no UAPP code or assets were reused.

## 1. The problem UAPP solves on Android

On a normal Android player, PCM goes through the system audio stack:

```
App ──► AudioTrack ──► AudioFlinger (system mixer) ──► Audio HAL ──► kernel USB-audio (snd-usb-audio) ──► DAC
                         │
                         ├─ resamples everything to the mixer rate (usually 48 kHz)
                         ├─ mixes with notification / system sounds
                         ├─ applies software volume, effects, and EQ (float)
                         └─ often limited to 16- or 24-bit / 48 kHz on the USB path
```

So a 24-bit / 96 kHz or 44.1 kHz FLAC almost never reaches the DAC unchanged. Until Android 14's
`AudioMixerAttributes` bit-perfect mode, an app had no supported way to get bit-perfect output.

## 2. UAPP's approach: its own user-space USB Audio driver

UAPP **bypasses AudioFlinger completely** and talks to the DAC itself:

1. **Claim the device.** It uses the Android USB Host API (`UsbManager.requestPermission`,
   `UsbDeviceConnection`). This is why UAPP asks "Allow UAPP to access the USB device?". It then claims the
   DAC's *Audio Streaming* interface, which detaches the kernel driver. From then on, no other app can play
   through that DAC. This is **exclusive mode**.
2. **Parse the descriptors.** It reads the USB Audio Class 1/2 descriptors (Audio Control and Audio
   Streaming interfaces, Format Type descriptors, and UAC2 Clock Source entities). From these it learns
   exactly which bit depths, channel counts and sample rates each *alternate setting* supports.
3. **Negotiate the format to match the file.** It picks the alternate setting whose sub-slot size / bit
   resolution and channel count match the track. It then programs the sample rate: UAC2 `SET_CUR` on the
   Clock Source (`CS_SAM_FREQ_CONTROL`), or UAC1 `SET_CUR` on the endpoint's sampling-frequency control.
   The DAC runs at the **file's native rate**, so nothing is resampled.
4. **Stream isochronous USB packets itself.** The Java USB API has no isochronous transfers, so native
   code (libusb-style) submits URBs through the device file descriptor (usbdevfs ioctls). For asynchronous
   DACs it reads the **feedback endpoint** and adjusts the samples per packet, so the DAC's clock stays the
   master.
5. **Keep the sample data unchanged.** In *bit-perfect mode* UAPP disables everything that alters the
   data: EQ, resampling, software volume, and ReplayGain. Volume goes to the DAC's **hardware Feature Unit volume control**
   instead, so the PCM words stay untouched. Lower-resolution content is only *zero-padded* into the
   container the DAC wants (for example, 16-bit inside 24- or 32-bit sub-slots), which is still bit-perfect.
6. **Change format between tracks only when needed.** If the next track has the same format, it keeps
   streaming (gapless). If the format differs, it stops the stream, re-selects the alt-setting and clock,
   and restarts.

It also has DoP/native DSD output, MQA decoding, and a "Hi-Res direct driver" for some phones' internal
DACs. These are extras on top of the same principle: **own the hardware path and never let the OS mixer
touch the samples.**

## 3. The Windows equivalent

Windows has the same kind of shared mixer as AudioFlinger: the Audio Engine (`audiodg.exe`) resamples
everything to the "Default Format" from the Sound control panel and mixes in float. It also has the cure
built in:

| UAPP on Android                                   | This app on Windows                                                      |
|---------------------------------------------------|--------------------------------------------------------------------------|
| Own USB driver claims the DAC                     | **WASAPI exclusive mode** (`AUDCLNT_SHAREMODE_EXCLUSIVE`): the app owns the endpoint, and the Audio Engine/mixer is bypassed |
| Parse UAC descriptors for supported formats       | `IAudioClient::IsFormatSupported(EXCLUSIVE, WAVEFORMATEXTENSIBLE)` probes the driver (`usbaudio2.sys` reads the same descriptors) |
| Select alt-setting and program the clock rate     | `IAudioClient::Initialize` with the exact rate, bit depth and container. The class driver selects the alt-setting and sets the clock |
| Isochronous URBs and feedback endpoint            | Done by Windows' in-box UAC2 driver. The app fills the driver's DMA buffer in **event-driven** mode |
| Zero-pad 16-bit into 24/32-bit sub-slots           | Candidate list tries 16→24→24-in-32→32 containers, with MSB-aligned zero padding |
| Hardware volume (Feature Unit)                    | `IAudioEndpointVolume` (the driver maps it to the UAC Feature Unit when the device has one) |
| Software DSP disabled in bit-perfect mode         | Integer pass-through path. Software volume is used only when the user picks it, and the UI then drops the BIT-PERFECT badge |
| Gapless when format is equal, re-init when not    | Same: the next decoder is pre-started and switched sample-accurately inside one stream. The stream is re-initialised on a format change |

A custom user-mode USB driver on Windows (WinUSB) would have to *replace* the DAC's driver for every
app, which is not acceptable. WASAPI exclusive gives the same bit-perfect result through the official path.
(ASIO and Kernel Streaming are other routes. They are not needed for bit-perfect output.)

## 4. Format negotiation used by this app (mirrors UAPP's alt-setting choice)

For a track at rate *R*, *C* channels and *B* bits, candidates are tried in this order:

1. Channels: *C*, then 2 (mono is **duplicated** to L/R, which is still bit-perfect; multichannel falls back to a stereo downmix).
2. Rates: *R* first, then higher rates of the same family (44.1k → 88.2k → 176.4k …), then the other family, then lower rates.
3. Containers (lossless options first):
   - B ≤ 16: 16 → 24 → 24-in-32 → 32 → float
   - B ≤ 24: 24 → 24-in-32 → 32 → float → 16 (dithered)
   - B = 32: 32 → float → 24-in-32 → 24 → 16
   - float/lossy decoders: float → 32 → 24-in-32 → 24 → 16

The first format that passes both `IsFormatSupported` **and** `Initialize` wins.
The `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` re-initialise dance is handled.
**BIT-PERFECT** is shown only when all of these hold:
exclusive mode, native rate, same channels (or mono duplicated), a lossless integer source that fits the
container, and software volume at exactly 100% or hardware volume in use.

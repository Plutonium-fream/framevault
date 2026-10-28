# Video test fixture

`video-fixture.mp4` is a synthetic video generated with FFmpeg for FrameVault's
automated checks. It contains no third-party footage or user workspace data.
It is a three-second, 320×180 H.264 MP4 at 24 fps.

Both check projects copy it to their build output. Storage checks compare decoded
frame hashes before and after saving a workspace, as well as checking that the
original source bytes stay unchanged. The app and portable package do not include
this fixture. FFmpeg itself is not distributed with FrameVault.

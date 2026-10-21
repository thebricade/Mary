// Tiny macOS webcam plugin for Unity: runs Apple's Vision face-landmark detection on the built-in camera
// and exposes the raw numbers (eye openness, pupil position, head yaw) through a C API.
// Blink and gaze interpretation happens in C# (WebcamGazeSource.cs).
//
// Build with ./build.sh

#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <Vision/Vision.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>

#define EXPORT __attribute__((visibility("default")))

// State codes returned by wg_state()
enum { kIdle = 0, kRequesting = 1, kRunning = 2, kDenied = 3, kNoCamera = 4 };

// Values written by wg_poll()
enum {
    kFace = 0,        // 1 when a face was found in the latest frame
    kLeftOpen,        // eyelid opening: eye height / eye width (about 0.25-0.35 open, below ~0.15 closed)
    kRightOpen,
    kLeftPupil,       // pupil x inside the eye, 0 = one corner, 1 = the other
    kRightPupil,
    kYaw,             // head turn in radians
    kPitch,
    kFaceX,           // face centre across the image, 0..1
    kLeftIris,        // iris centre inside the eye measured from the image itself, 0..1 (-1 = no reading)
    kRightIris,
    kValueCount
};

// What the iris search looked at, so a snapshot can show it (see wg_dump).
typedef struct { double minX, maxX, minY, maxY; int x0, x1, y0, y1; float iris; int valid; } EyeDebug;
static EyeDebug gDbg[2];

// Finds the dark iris inside one eye and returns where it sits between the eye corners (0..1), or -1.
// The eye outline comes from Vision; the iris position is measured on the actual pixels, so it follows the
// eyeball and not the head.
static float IrisRatio(VNFaceLandmarkRegion2D *eye, CGRect box, const uint8_t *base, size_t rowBytes, size_t imgW, size_t imgH, EyeDebug *dbg)
{
    dbg->valid = 0;
    if (!eye || eye.pointCount < 4) return -1;
    const CGPoint *p = eye.normalizedPoints;
    double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
    for (NSUInteger i = 0; i < eye.pointCount; i++) {
        double x = (box.origin.x + p[i].x * box.size.width) * imgW;
        double y = (1.0 - (box.origin.y + p[i].y * box.size.height)) * imgH;   // Vision's y runs up, the pixel buffer's runs down
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    double w = maxX - minX, h = maxY - minY;
    dbg->minX = minX; dbg->maxX = maxX; dbg->minY = minY; dbg->maxY = maxY; dbg->valid = 1; dbg->iris = -1;
    if (w < 14 || h < 3) return -1;                       // too small, or the eye is shut

    // Look only at the middle of the eye: skip the corners (shadow) and the lid lines (lashes) that would drag the answer toward the centre.
    int x0 = (int)(minX + 0.12 * w), x1 = (int)(maxX - 0.12 * w);
    int y0 = (int)(minY + 0.25 * h), y1 = (int)(maxY - 0.25 * h);
    if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0;
    if (x1 >= (int)imgW) x1 = (int)imgW - 1; if (y1 >= (int)imgH) y1 = (int)imgH - 1;
    dbg->x0 = x0; dbg->x1 = x1; dbg->y0 = y0; dbg->y1 = y1;
    if (x1 - x0 < 6 || y1 < y0) return -1;

    // Brightness of each pixel (BGRA input).
    int cols = x1 - x0 + 1, rows = y1 - y0 + 1;
    float lum[cols * rows];
    float lo = 1e9, hi = -1e9;
    for (int y = 0; y < rows; y++) {
        const uint8_t *row = base + (size_t)(y0 + y) * rowBytes;
        for (int x = 0; x < cols; x++) {
            const uint8_t *px = row + (size_t)(x0 + x) * 4;
            float l = 0.114f * px[0] + 0.587f * px[1] + 0.299f * px[2];
            lum[y * cols + x] = l;
            if (l < lo) lo = l; if (l > hi) hi = l;
        }
    }
    if (hi - lo < 20) return -1;                          // flat, no visible iris

    // Darkest quarter of the range = iris and pupil. Their weighted centre is the gaze direction within the eye.
    float thr = lo + (hi - lo) * 0.28f;
    double sum = 0, wsum = 0;
    for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++) {
            float l = lum[y * cols + x];
            if (l < thr) { double wgt = thr - l; sum += wgt * (x0 + x + 0.5); wsum += wgt; }
        }
    if (wsum <= 0) return -1;
    dbg->iris = (float)(sum / wsum);
    return (float)((sum / wsum - minX) / w);
}

static char gDumpPath[1024];
static int gDumpRequested = 0;

static void WriteSnapshot(const uint8_t *base, size_t rowBytes, size_t w, size_t h)
{
    CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
    CGContextRef ctx = CGBitmapContextCreate((void *)base, w, h, 8, rowBytes, cs,
                                             kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little);
    if (ctx) {
        // CoreGraphics' y runs up; ours runs down, so flip when drawing the annotations.
        for (int e = 0; e < 2; e++) {
            EyeDebug *d = &gDbg[e];
            if (!d->valid) continue;
            CGContextSetLineWidth(ctx, 2);
            CGContextSetRGBStrokeColor(ctx, 0, 1, 0, 1);       // green: eye outline from Vision
            CGContextStrokeRect(ctx, CGRectMake(d->minX, h - d->maxY, d->maxX - d->minX, d->maxY - d->minY));
            CGContextSetRGBStrokeColor(ctx, 1, 1, 0, 1);       // yellow: the part searched for the iris
            CGContextStrokeRect(ctx, CGRectMake(d->x0, h - d->y1, d->x1 - d->x0, d->y1 - d->y0));
            if (d->iris >= 0) {
                CGContextSetRGBStrokeColor(ctx, 1, 0, 0, 1);   // red: where the iris was found
                double cx = d->iris, cy = h - (d->y0 + d->y1) * 0.5;
                CGContextStrokeRect(ctx, CGRectMake(cx - 2, cy - 12, 4, 24));
            }
        }
        CGImageRef img = CGBitmapContextCreateImage(ctx);
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:gDumpPath]];
        CGImageDestinationRef dest = CGImageDestinationCreateWithURL((__bridge CFURLRef)url, CFSTR("public.png"), 1, NULL);
        if (dest && img) { CGImageDestinationAddImage(dest, img, NULL); CGImageDestinationFinalize(dest); }
        if (dest) CFRelease(dest);
        if (img) CGImageRelease(img);
        CGContextRelease(ctx);
    }
    CGColorSpaceRelease(cs);
}

@interface WGCapture : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate>
@property (nonatomic, strong) AVCaptureSession *session;
@property (nonatomic, strong) dispatch_queue_t queue;
@property (nonatomic, strong) VNDetectFaceLandmarksRequest *request;
@end

static WGCapture *gCapture = nil;
static int gState = kIdle;
static int gFrame = 0;
static float gValues[kValueCount];
static NSObject *gLock = nil;

static NSObject *Lock(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{ gLock = [NSObject new]; });
    return gLock;
}

static void EyeStats(VNFaceLandmarkRegion2D *eye, VNFaceLandmarkRegion2D *pupil, CGRect box,
                     size_t imgW, size_t imgH, float *open, float *pupilX)
{
    *open = 0; *pupilX = 0.5f;
    if (!eye || eye.pointCount < 4) return;
    const CGPoint *p = eye.normalizedPoints;
    double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
    for (NSUInteger i = 0; i < eye.pointCount; i++) {
        // normalizedPoints are relative to the face box; convert to pixels so width/height keep their proportions
        double x = p[i].x * box.size.width * imgW;
        double y = p[i].y * box.size.height * imgH;
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    double w = maxX - minX, h = maxY - minY;
    if (w > 1e-3) *open = (float)(h / w);
    if (pupil && pupil.pointCount > 0 && w > 1e-3) {
        double px = pupil.normalizedPoints[0].x * box.size.width * imgW;
        *pupilX = (float)((px - minX) / w);
    }
}

@implementation WGCapture

- (void)captureOutput:(AVCaptureOutput *)output didOutputSampleBuffer:(CMSampleBufferRef)buffer fromConnection:(AVCaptureConnection *)connection
{
    CVPixelBufferRef pixels = CMSampleBufferGetImageBuffer(buffer);
    if (!pixels) return;
    size_t w = CVPixelBufferGetWidth(pixels), h = CVPixelBufferGetHeight(pixels);

    CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    const uint8_t *base = (const uint8_t *)CVPixelBufferGetBaseAddress(pixels);
    size_t rowBytes = CVPixelBufferGetBytesPerRow(pixels);

    VNImageRequestHandler *handler = [[VNImageRequestHandler alloc] initWithCVPixelBuffer:pixels options:@{}];
    NSError *err = nil;
    [handler performRequests:@[self.request] error:&err];

    float v[kValueCount] = {0};
    VNFaceObservation *face = self.request.results.firstObject;
    if (face && face.landmarks) {
        v[kFace] = 1;
        EyeStats(face.landmarks.leftEye,  face.landmarks.leftPupil,  face.boundingBox, w, h, &v[kLeftOpen],  &v[kLeftPupil]);
        EyeStats(face.landmarks.rightEye, face.landmarks.rightPupil, face.boundingBox, w, h, &v[kRightOpen], &v[kRightPupil]);
        if (base) {
            v[kLeftIris]  = IrisRatio(face.landmarks.leftEye,  face.boundingBox, base, rowBytes, w, h, &gDbg[0]);
            v[kRightIris] = IrisRatio(face.landmarks.rightEye, face.boundingBox, base, rowBytes, w, h, &gDbg[1]);
        } else { v[kLeftIris] = v[kRightIris] = -1; }
        v[kYaw]   = face.yaw   ? face.yaw.floatValue   : 0;
        v[kPitch] = face.pitch ? face.pitch.floatValue : 0;
        v[kFaceX] = (float)CGRectGetMidX(face.boundingBox);
    }
    if (gDumpRequested && base) { gDumpRequested = 0; WriteSnapshot(base, rowBytes, w, h); }
    CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    @synchronized (Lock()) {
        memcpy(gValues, v, sizeof(v));
        gFrame++;
    }
}

@end

static AVCaptureDevice *BuiltInCamera(void)
{
    // Prefer the Mac's own camera over a Continuity Camera (iPhone) that happens to be nearby.
    AVCaptureDeviceDiscoverySession *found = [AVCaptureDeviceDiscoverySession
        discoverySessionWithDeviceTypes:@[AVCaptureDeviceTypeBuiltInWideAngleCamera]
                              mediaType:AVMediaTypeVideo
                               position:AVCaptureDevicePositionUnspecified];
    return found.devices.firstObject ?: [AVCaptureDevice defaultDeviceWithMediaType:AVMediaTypeVideo];
}

static void StartSession(void)
{
    AVCaptureDevice *device = BuiltInCamera();
    if (!device) { gState = kNoCamera; return; }

    NSError *err = nil;
    AVCaptureDeviceInput *input = [AVCaptureDeviceInput deviceInputWithDevice:device error:&err];
    if (!input) { gState = kNoCamera; return; }

    WGCapture *cap = [WGCapture new];
    cap.queue = dispatch_queue_create("webcamgaze.capture", DISPATCH_QUEUE_SERIAL);
    cap.request = [VNDetectFaceLandmarksRequest new];
    cap.session = [AVCaptureSession new];
    cap.session.sessionPreset = AVCaptureSessionPreset1280x720;   // eyes need pixels to find the iris

    AVCaptureVideoDataOutput *output = [AVCaptureVideoDataOutput new];
    output.alwaysDiscardsLateVideoFrames = YES;
    output.videoSettings = @{ (id)kCVPixelBufferPixelFormatTypeKey : @(kCVPixelFormatType_32BGRA) };
    [output setSampleBufferDelegate:cap queue:cap.queue];

    if (![cap.session canAddInput:input] || ![cap.session canAddOutput:output]) { gState = kNoCamera; return; }
    [cap.session addInput:input];
    [cap.session addOutput:output];
    [cap.session startRunning];

    gCapture = cap;
    gState = kRunning;
}

EXPORT int wg_start(void)
{
    if (gState == kRunning || gState == kRequesting) return gState;
    AVAuthorizationStatus status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo];
    if (status == AVAuthorizationStatusDenied || status == AVAuthorizationStatusRestricted) { gState = kDenied; return gState; }
    if (status == AVAuthorizationStatusAuthorized) { StartSession(); return gState; }

    gState = kRequesting;
    [AVCaptureDevice requestAccessForMediaType:AVMediaTypeVideo completionHandler:^(BOOL granted) {
        if (granted) StartSession(); else gState = kDenied;
    }];
    return gState;
}

EXPORT void wg_stop(void)
{
    if (gCapture) { [gCapture.session stopRunning]; gCapture = nil; }
    gState = kIdle;
}

EXPORT int wg_state(void) { return gState; }

// Copies the latest values into out (kValueCount floats) and returns the frame counter.
EXPORT int wg_poll(float *out)
{
    @synchronized (Lock()) {
        memcpy(out, gValues, sizeof(gValues));
        return gFrame;
    }
}

// Saves the next camera frame as a PNG at path, with the eye boxes and iris marks drawn on it.
EXPORT void wg_dump(const char *path)
{
    strncpy(gDumpPath, path, sizeof(gDumpPath) - 1);
    gDumpRequested = 1;
}

EXPORT int wg_value_count(void) { return kValueCount; }

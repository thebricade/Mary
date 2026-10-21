// Terminal test for the plugin: prints the live values for a few seconds.  ./build.sh test
#import <Foundation/Foundation.h>
int wg_start(void); void wg_stop(void); int wg_state(void); int wg_poll(float *out);
int main(int argc, char **argv) {
    @autoreleasepool {
        int secs = argc > 1 ? atoi(argv[1]) : 15;
        printf("start -> state %d\n", wg_start());
        float v[16]; int last = -1;
        for (int i = 0; i < secs * 10; i++) {
            [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.1]];
            int f = wg_poll(v);
            if (f != last) { last = f;
                printf("state %d frame %4d face %.0f  open L %.2f R %.2f  pupil L %.2f R %.2f  yaw %+.2f\n",
                       wg_state(), f, v[0], v[1], v[2], v[3], v[4], v[5]); }
            else if (i % 10 == 0) printf("state %d (no frames yet)\n", wg_state());
            fflush(stdout);
        }
        wg_stop();
    }
    return 0;
}

#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

// Called once per request: 0 = accepted, 1 = denied, 2 = error, 3 = banners disabled.
typedef void (*PCNotificationResult)(int, const char *, void *);
@interface PCNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end
@implementation PCNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
      willPresentNotification:(UNNotification *)notification
        withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler {
    completionHandler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList |
                      UNNotificationPresentationOptionSound);
}
@end

__attribute__((visibility("default")))
void pc_notify(const char *title, const char *body, PCNotificationResult callback, void *context) {
    // Copy UTF-8 strings before returning to managed code; blocks retain these copies.
    NSString *heading = [NSString stringWithUTF8String:title];
    NSString *message = [NSString stringWithUTF8String:body];
    dispatch_async(dispatch_get_main_queue(), ^{
        @try {
            UNUserNotificationCenter *center = UNUserNotificationCenter.currentNotificationCenter;
            static PCNotificationDelegate *delegate;
            static dispatch_once_t once;
            dispatch_once(&once, ^{ delegate = [PCNotificationDelegate new]; });
            center.delegate = delegate;
            [center requestAuthorizationWithOptions:UNAuthorizationOptionAlert | UNAuthorizationOptionSound
                completionHandler:^(BOOL granted, NSError *error) {
                    if (error) { callback(2, error.localizedDescription.UTF8String, context); return; }
                    if (!granted) { callback(1, "Notification permission denied", context); return; }
                    [center getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) {
                        UNMutableNotificationContent *content = [UNMutableNotificationContent new];
                        content.title = heading;
                        content.body = message;
                        content.sound = UNNotificationSound.defaultSound;
                        UNNotificationRequest *request = [UNNotificationRequest
                            requestWithIdentifier:NSUUID.UUID.UUIDString content:content trigger:nil];
                        [center addNotificationRequest:request withCompletionHandler:^(NSError *failure) {
                            if (failure) callback(2, failure.localizedDescription.UTF8String, context);
                            else if (settings.alertSetting != UNNotificationSettingEnabled ||
                                     settings.alertStyle == UNAlertStyleNone)
                                callback(3, "Notification accepted but banners are disabled", context);
                            else callback(0, "Notification accepted by macOS", context);
                        }];
                    }];
                }];
        } @catch (NSException *exception) {
            callback(2, exception.reason.UTF8String, context);
        }
    });
}

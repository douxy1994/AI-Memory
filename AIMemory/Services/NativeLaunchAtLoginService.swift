import ServiceManagement

enum LaunchAtLoginState: Equatable, Sendable {
    case disabled
    case enabled
    case requiresApproval
    case unavailable

    var isEnabled: Bool {
        self == .enabled
    }

    var detail: String {
        switch self {
        case .disabled:
            "关闭"
        case .enabled:
            "已开启，登录 macOS 后会自动启动 AI Memory"
        case .requiresApproval:
            "等待在“系统设置 → 通用 → 登录项与扩展”中批准"
        case .unavailable:
            "当前应用副本无法注册；请从“应用程序”文件夹运行已签名版本"
        }
    }
}

@MainActor
struct NativeLaunchAtLoginService {
    private var service: SMAppService { .mainApp }

    var state: LaunchAtLoginState {
        switch service.status {
        case .notRegistered:
            .disabled
        case .enabled:
            .enabled
        case .requiresApproval:
            .requiresApproval
        case .notFound:
            .unavailable
        @unknown default:
            .unavailable
        }
    }

    func setEnabled(_ enabled: Bool) throws -> LaunchAtLoginState {
        if enabled {
            if service.status != .enabled {
                try service.register()
            }
        } else if service.status != .notRegistered {
            try service.unregister()
        }
        return state
    }

    func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}

import Foundation

enum AsyncCancellation {
    /// Leaving a SwiftUI page cancels its .task and any URLSession request it owns.
    static func isExpected(_ error: Error) -> Bool {
        let nsError = error as NSError
        return error is CancellationError
            || (nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled)
    }
}

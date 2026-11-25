import type * as React from "react";

/// The bare card layout of the public token pages (intake form, survey,
/// campaign answer): no session, no app shell, just the content.
export function PublicShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="app-background sd-public-shell min-h-screen w-full px-4 py-12">
      <div className="mx-auto max-w-xl rounded-2xl border border-glass-strong bg-glass p-8 shadow-2xl backdrop-blur-xl">
        {children}
      </div>
    </div>
  );
}

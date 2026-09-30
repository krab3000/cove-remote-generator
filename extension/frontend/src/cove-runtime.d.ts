// The parts of Cove's extension runtime this bundle uses. See Cove's
// docs-site/.../developer/extensions/frontend-runtime.mdx and ui/src/components/extension-shared.ts.

declare module "@cove/runtime/api" {
  export interface ExtensionFetchOptions extends RequestInit {
    timeoutMs?: number | null;
  }
  export function extensionFetch(input: string, init?: ExtensionFetchOptions): Promise<Response>;
}

declare module "@cove/runtime/components" {
  export interface CovePathConfig {
    path: string;
    excludeVideo: boolean;
  }
  export function useAppConfig(): {
    config?: { covePaths?: CovePathConfig[] };
  };
}

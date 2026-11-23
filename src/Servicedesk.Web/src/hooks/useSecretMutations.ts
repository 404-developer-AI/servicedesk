import { useMutation, useQueryClient, type QueryKey } from "@tanstack/react-query";
import { toast } from "sonner";
import { errorMessage } from "@/lib/http";

type SecretMutationOptions = {
  /// What the secret is called in the UI, e.g. "API key", "Client secret".
  label: string;
  /// Stores the current draft. Reads the draft from the caller's render, so
  /// call sites keep `save.mutate()` without arguments.
  save: () => Promise<unknown>;
  clear: () => Promise<unknown>;
  /// Query keys to refresh after either action (secret state, status, …).
  invalidate: QueryKey[];
  /// Extra work after a successful save (typically: empty the draft field).
  onSaved?: () => void;
  /// Extra work after a successful clear (e.g. drop cached test results).
  onCleared?: () => void;
};

/// The save / clear pair every integration settings page needs for its
/// stored secret (`protected_secrets`). One place for the toasts, the
/// server-message error handling and the cache refresh — a clear that fails
/// is never silent. The secret value itself never leaves the mutation:
/// nothing is logged or echoed back.
export function useSecretMutations({ label, save, clear, invalidate, onSaved, onCleared }: SecretMutationOptions) {
  const qc = useQueryClient();
  const refresh = () => {
    for (const queryKey of invalidate) qc.invalidateQueries({ queryKey });
  };

  const saveMutation = useMutation({
    mutationFn: save,
    onSuccess: () => {
      toast.success(`${label} saved`);
      onSaved?.();
      refresh();
    },
    onError: (err) => toast.error(errorMessage(err, `${label} could not be saved`)),
  });

  const clearMutation = useMutation({
    mutationFn: clear,
    onSuccess: () => {
      toast.success(`${label} cleared`);
      onCleared?.();
      refresh();
    },
    onError: (err) => toast.error(errorMessage(err, `${label} could not be cleared`)),
  });

  return { save: saveMutation, clear: clearMutation };
}

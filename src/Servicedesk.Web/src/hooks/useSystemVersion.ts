import { useQuery } from "@tanstack/react-query";
import { systemApi } from "@/lib/api";

export function useSystemVersion() {
  return useQuery({
    queryKey: ["system", "version"],
    queryFn: systemApi.version,
    staleTime: Infinity,
    gcTime: Infinity,
    retry: 1,
  });
}

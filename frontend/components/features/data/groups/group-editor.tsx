"use client";

import { useRef, useEffect, useState, useCallback } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { EditorView } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { json, jsonParseLinter } from "@codemirror/lang-json";
import { oneDark } from "@codemirror/theme-one-dark";
import { linter } from "@codemirror/lint";
import { basicSetup } from "codemirror";
import { dataApi, DataApiError } from "@/lib/services/data-api";
import { useToast } from "@/components/ui/toast";
import { Button } from "@/components/ui/button";
import { ValidatePreviewPanel } from "./validate-preview";
import type { CollectionGroupDoc, ValidatePreview } from "@/types/data-tab";

// Same recipe as run-new-panel.tsx.
const EDITOR_EXTENSIONS = [
  basicSetup,
  json(),
  linter(jsonParseLinter()),
  oneDark,
  EditorView.theme({
    "&": { height: "100%" },
    ".cm-scroller": { overflow: "auto" },
  }),
];

const TEMPLATE_JSON = JSON.stringify(
  {
    name: "my-group",
    enabled: true,
    exchanges: ["binance"],
    assets: { symbols: ["BTC/USDT-PERP"], historyStart: "2024-01" },
    feeds: {
      candles: { collect: "eager", intervals: ["1m", "1h"] },
      "funding-rate": { collect: "eager" },
    },
    derived: {},
  },
  null,
  2,
);

export interface GroupEditorProps {
  mode: "create" | "edit";
  /** Edit mode: name of the group to load via getGroup. */
  name?: string;
  onClose: () => void;
  /** Called after a successful save so the parent can close and refetch. */
  onSaved: () => void;
}

export function GroupEditor({ mode, name, onClose, onSaved }: GroupEditorProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const editorContainerRef = useRef<HTMLDivElement>(null);
  const editorViewRef = useRef<EditorView | null>(null);

  // docText is the source of truth for Validate/Save; CodeMirror syncs back into it.
  const [docText, setDocText] = useState(mode === "create" ? TEMPLATE_JSON : "");
  const [etag, setEtag] = useState<string | undefined>(undefined);

  const [validateResult, setValidateResult] = useState<ValidatePreview | null>(null);
  const [validateErrors, setValidateErrors] = useState<string[]>([]);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [isValidating, setIsValidating] = useState(false);
  const [isSaving, setIsSaving] = useState(false);

  const groupQuery = useQuery({
    queryKey: ["data", "group", name],
    queryFn: ({ signal }) => dataApi.getGroup(name!, signal),
    enabled: mode === "edit" && name !== undefined,
  });

  // When the group loads (edit mode), populate docText + etag and update the editor.
  useEffect(() => {
    if (!groupQuery.data) return;
    const text = JSON.stringify(groupQuery.data.group, null, 2);
    setDocText(text);
    setEtag(groupQuery.data.etag);
    if (editorViewRef.current) {
      editorViewRef.current.dispatch({
        changes: { from: 0, to: editorViewRef.current.state.doc.length, insert: text },
      });
    }
  }, [groupQuery.data]);

  // Mount CodeMirror once the container div is available.
  useEffect(() => {
    if (!editorContainerRef.current) return;
    if (editorViewRef.current) return;

    const initialDoc = mode === "create" ? TEMPLATE_JSON : docText;

    const state = EditorState.create({
      doc: initialDoc,
      extensions: [
        ...EDITOR_EXTENSIONS,
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            setDocText(update.state.doc.toString());
          }
        }),
      ],
    });

    const view = new EditorView({ state, parent: editorContainerRef.current });
    editorViewRef.current = view;

    return () => {
      view.destroy();
      editorViewRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- mount once; doc updates via dispatch
  }, []);

  const handleValidate = useCallback(async () => {
    let doc: CollectionGroupDoc;
    try {
      doc = JSON.parse(docText) as CollectionGroupDoc;
    } catch {
      setValidateErrors(["Invalid JSON — fix syntax before validating"]);
      return;
    }

    setValidateErrors([]);
    setValidateResult(null);
    setIsValidating(true);

    try {
      const result = await dataApi.validateGroup(doc);
      if (result.errors.length > 0) {
        setValidateErrors(result.errors);
      } else {
        setValidateResult(result);
      }
    } catch (err) {
      if (err instanceof DataApiError) {
        const body = err.body as { errors?: string[] } | null;
        setValidateErrors(body?.errors ?? [err.message]);
      } else {
        setValidateErrors([err instanceof Error ? err.message : "Validation failed"]);
      }
    } finally {
      setIsValidating(false);
    }
  }, [docText]);

  const handleSave = useCallback(async () => {
    let doc: CollectionGroupDoc;
    try {
      doc = JSON.parse(docText) as CollectionGroupDoc;
    } catch {
      setSaveErrors(["Invalid JSON — fix syntax before saving"]);
      return;
    }

    setSaveErrors([]);
    setIsSaving(true);

    try {
      const groupName = name ?? doc.name;
      await dataApi.putGroup(groupName, doc, etag);
      await queryClient.invalidateQueries({ queryKey: ["data", "groups"] });
      await queryClient.invalidateQueries({ queryKey: ["data", "desired-state"] });
      toast("Group saved", "success");
      onSaved();
    } catch (err) {
      if (err instanceof DataApiError) {
        if (err.status === 409) {
          toast("group changed on server — reload", "error");
          return;
        }
        if (err.status === 422) {
          const body = err.body as { errors?: string[] } | null;
          setSaveErrors(body?.errors ?? [err.message]);
          return;
        }
      }
      toast(err instanceof Error ? err.message : "Save failed", "error");
    } finally {
      setIsSaving(false);
    }
  }, [docText, etag, name, toast, onSaved, queryClient]);

  const isEditLoading = mode === "edit" && groupQuery.isLoading;
  const editError = mode === "edit" ? groupQuery.error : null;
  // True once docText is populated and the editor is ready for interaction.
  const isReady = mode === "create" || docText !== "";

  return (
    <div className="flex flex-col h-full gap-4">
      {isEditLoading && (
        <div className="text-text-secondary text-sm">Loading group…</div>
      )}

      {isReady && mode === "edit" && (
        <div className="text-xs text-text-muted" data-testid="editor-ready">
          Editing: {name}
        </div>
      )}

      {editError && (
        <div role="alert" className="text-accent-red text-sm">
          Failed to load group:{" "}
          {editError instanceof Error ? editError.message : String(editError)}
        </div>
      )}

      {/* Editor container is always rendered so the mount effect can attach CodeMirror. */}
      <div
        ref={editorContainerRef}
        className={`border border-border-default rounded overflow-hidden min-h-64 flex-1${isEditLoading ? " invisible" : ""}`}
      />

      {saveErrors.length > 0 && (
        <ul className="space-y-1">
          {saveErrors.map((e, i) => (
            <li key={i} role="alert" className="text-accent-red text-sm">
              {e}
            </li>
          ))}
        </ul>
      )}

      {validateErrors.length > 0 && (
        <ul className="space-y-1">
          {validateErrors.map((e, i) => (
            <li key={i} role="alert" className="text-accent-red text-sm">
              {e}
            </li>
          ))}
        </ul>
      )}

      {validateResult && <ValidatePreviewPanel preview={validateResult} />}

      <div className="flex gap-2">
        <Button
          variant="secondary"
          onClick={handleValidate}
          loading={isValidating}
          disabled={isEditLoading}
        >
          Validate
        </Button>
        <Button onClick={handleSave} loading={isSaving} disabled={isEditLoading}>
          Save
        </Button>
        <Button variant="ghost" onClick={onClose}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

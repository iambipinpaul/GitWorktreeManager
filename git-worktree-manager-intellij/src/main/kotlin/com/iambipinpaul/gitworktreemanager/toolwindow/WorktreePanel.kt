package com.iambipinpaul.gitworktreemanager.toolwindow

import com.iambipinpaul.gitworktreemanager.dialogs.AddWorktreeDialog
import com.iambipinpaul.gitworktreemanager.dialogs.DeleteConfirmationDialog
import com.iambipinpaul.gitworktreemanager.models.GitCommandResult
import com.iambipinpaul.gitworktreemanager.models.Worktree
import com.iambipinpaul.gitworktreemanager.notifications.NotificationService
import com.iambipinpaul.gitworktreemanager.services.GitService
import com.intellij.icons.AllIcons
import com.intellij.ide.actions.RevealFileAction
import com.intellij.ide.impl.ProjectUtil
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.ui.DocumentAdapter
import com.intellij.ui.JBColor
import com.intellij.ui.SearchTextField
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBPanel
import com.intellij.ui.components.JBScrollPane
import com.intellij.util.ui.AsyncProcessIcon
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.UIUtil
import kotlinx.coroutines.runBlocking
import java.awt.BorderLayout
import java.awt.Color
import java.awt.Component
import java.awt.Dimension
import java.awt.FlowLayout
import java.awt.Font
import java.awt.datatransfer.StringSelection
import java.io.File
import javax.swing.Box
import javax.swing.BoxLayout
import javax.swing.JButton
import javax.swing.JPanel
import javax.swing.event.DocumentEvent

class WorktreePanel(private val project: Project) : JBPanel<WorktreePanel>(BorderLayout()) {
    private val gitService = GitService()
    private val notifications = NotificationService(project)
    private val searchField = SearchTextField()
    private val loadingIcon = AsyncProcessIcon("GitWorktrees.Loading")
    private val listPanel = JBPanel<JBPanel<*>>()
    private val scrollPane = JBScrollPane(listPanel)
    private val emptyStateLabel = JBLabel()

    // JBColor.lazy defers resolution to paint time so the color tracks L&F changes.
    private val panelBackground = JBColor.lazy { UIUtil.getPanelBackground() }
    private val cardBackground = JBColor.lazy { UIUtil.getListBackground() }

    private var repositoryRoot: String? = null
    private var allWorktrees: List<Worktree> = emptyList()
    private var emptyStateMessage: String? = null

    private val refreshAction = object : AnAction("Refresh", "Refresh worktrees", AllIcons.Actions.Refresh) {
        override fun actionPerformed(e: AnActionEvent) {
            refreshWorktrees()
        }
    }

    private val addAction = object : AnAction("Add", "Add worktree", AllIcons.General.Add) {
        override fun actionPerformed(e: AnActionEvent) {
            showAddDialog()
        }
    }

    private val currentTagColors = TagColors(
        background = JBColor(Color(0xDFF4E5), Color(0x2B4B39)),
        foreground = JBColor(Color(0x1F7A3A), Color(0x9EE5B1)),
    )
    private val mainTagColors = TagColors(
        background = JBColor(Color(0xE3EEFF), Color(0x2C3F5C)),
        foreground = JBColor(Color(0x1F4E9D), Color(0x9CC2FF)),
    )
    private val neutralTagColors = TagColors(
        background = JBColor(Color(0xF0F0F0), Color(0x3A3A3A)),
        foreground = JBColor(Color(0x555555), Color(0xC0C0C0)),
    )

    init {
        listPanel.layout = BoxLayout(listPanel, BoxLayout.Y_AXIS)
        listPanel.border = JBUI.Borders.empty(6)
        listPanel.isOpaque = true
        listPanel.background = panelBackground
        background = panelBackground

        emptyStateLabel.foreground = JBColor.lazy { UIUtil.getContextHelpForeground() }
        emptyStateLabel.font = JBUI.Fonts.smallFont()
        emptyStateLabel.border = JBUI.Borders.empty(12)
        emptyStateLabel.alignmentX = Component.LEFT_ALIGNMENT

        loadingIcon.isVisible = false
        loadingIcon.suspend()

        val topToolbar = ActionManager.getInstance().createActionToolbar(
            "GitWorktrees.Top",
            DefaultActionGroup(addAction, refreshAction),
            true,
        )

        val rightPanel = JPanel()
        rightPanel.layout = BoxLayout(rightPanel, BoxLayout.X_AXIS)
        rightPanel.isOpaque = false
        rightPanel.add(searchField)
        rightPanel.add(Box.createHorizontalStrut(6))
        rightPanel.add(loadingIcon)

        val topPanel = JPanel(BorderLayout())
        topPanel.border = JBUI.Borders.empty(6)
        topPanel.add(topToolbar.component, BorderLayout.WEST)
        topPanel.add(rightPanel, BorderLayout.EAST)

        scrollPane.border = JBUI.Borders.empty()
        scrollPane.viewport.background = panelBackground

        add(topPanel, BorderLayout.NORTH)
        add(scrollPane, BorderLayout.CENTER)

        searchField.textEditor.document.addDocumentListener(object : DocumentAdapter() {
            override fun textChanged(e: DocumentEvent) {
                applyFilter()
            }
        })

        refreshWorktrees()
    }

    private fun refreshWorktrees() {
        setLoading(true)
        emptyStateMessage = "Loading worktrees..."
        if (allWorktrees.isEmpty()) {
            applyFilter()
        }
        runInBackground("Loading worktrees") {
            try {
                val basePath = project.basePath
                if (basePath.isNullOrBlank()) {
                    updateNoRepository()
                    return@runInBackground
                }

                val root = gitService.getRepositoryRoot(basePath)
                if (root.isNullOrBlank()) {
                    updateNoRepository()
                    return@runInBackground
                }

                repositoryRoot = root
                val result = gitService.getWorktrees(root)
                if (result is GitCommandResult.Success) {
                    val worktrees = result.data ?: emptyList()
                    updateWorktrees(worktrees)
                } else {
                    val message = result.errorMessage ?: "Failed to load worktrees"
                    notifications.error(message)
                    updateWorktrees(emptyList(), "Failed to load worktrees.")
                }
            } finally {
                setLoading(false)
            }
        }
    }

    private fun updateNoRepository() {
        ApplicationManager.getApplication().invokeLater {
            repositoryRoot = null
        }
        updateWorktrees(emptyList(), "No Git repository found for this project.")
    }

    private fun updateWorktrees(worktrees: List<Worktree>, emptyMessage: String? = null) {
        ApplicationManager.getApplication().invokeLater {
            allWorktrees = worktrees
            emptyStateMessage = emptyMessage
            applyFilter()
        }
    }

    private fun applyFilter() {
        val query = searchField.text.trim().lowercase()
        val filtered = if (query.isEmpty()) {
            allWorktrees
        } else {
            allWorktrees.filter {
                it.path.lowercase().contains(query) ||
                    (it.branch?.lowercase()?.contains(query) == true)
            }
        }

        listPanel.removeAll()

        val emptyText = when {
            emptyStateMessage != null -> emptyStateMessage
            allWorktrees.isEmpty() -> "No worktrees found."
            filtered.isEmpty() -> "No matching worktrees."
            else -> ""
        }

        if (filtered.isEmpty()) {
            emptyStateLabel.text = emptyText ?: ""
            listPanel.add(emptyStateLabel)
        } else {
            filtered.forEachIndexed { index, worktree ->
                val card = WorktreeCard(worktree)
                listPanel.add(card)
                if (index < filtered.size - 1) {
                    listPanel.add(Box.createVerticalStrut(8))
                }
            }
        }

        listPanel.revalidate()
        listPanel.repaint()
    }

    private fun showAddDialog() {
        val root = repositoryRoot
        if (root.isNullOrBlank()) {
            notifications.warn("Open a Git repository to add a worktree.")
            return
        }

        runInBackground("Loading branches") {
            val branchesResult = gitService.getBranches(root)
            val branches = if (branchesResult is GitCommandResult.Success) {
                branchesResult.data ?: emptyList()
            } else {
                emptyList()
            }

            ApplicationManager.getApplication().invokeLater {
                val dialog = AddWorktreeDialog(project, root, branches)
                if (dialog.showAndGet()) {
                    addWorktree(dialog, root)
                }
            }
        }
    }

    private fun addWorktree(dialog: AddWorktreeDialog, root: String) {
        runInBackground("Adding worktree") {
            val result = gitService.addWorktree(
                repositoryPath = root,
                worktreePath = dialog.worktreePath,
                branchName = dialog.branchName,
                createBranch = dialog.createBranch,
                baseBranch = dialog.baseBranch,
            )

            if (result.success) {
                notifications.info("Worktree created.")
                refreshWorktrees()
                if (dialog.openAfterCreation) {
                    openProject(dialog.worktreePath)
                }
            } else {
                notifications.error(result.errorMessage ?: "Failed to add worktree.")
            }
        }
    }

    private fun confirmAndRemove(worktree: Worktree) {
        val root = repositoryRoot
        if (root.isNullOrBlank()) {
            notifications.warn("Open a Git repository to remove a worktree.")
            return
        }

        if (!canRemove(worktree)) {
            notifications.warn("Main or current worktrees cannot be removed.")
            return
        }

        val message =
            "Are you sure you want to remove the worktree?\n\n" +
                "Branch: ${worktree.branch ?: "(detached)"}\n" +
                "Path: ${worktree.path}\n\n" +
                "This will delete the worktree folder and its contents."
        val choice = Messages.showOkCancelDialog(
            project,
            message,
            "Remove Worktree",
            Messages.getWarningIcon(),
        )

        if (choice != Messages.OK) {
            return
        }

        removeWorktree(root, worktree, false)
    }

    private fun removeWorktree(root: String, worktree: Worktree, force: Boolean) {
        runInBackground("Removing worktree") {
            val result = gitService.removeWorktree(root, worktree.path, force)
            if (result.success) {
                notifications.info("Worktree removed.")
                refreshWorktrees()
                return@runInBackground
            }

            val errorMessage = result.errorMessage ?: "Failed to remove worktree."
            val lower = errorMessage.lowercase()

            if (!force && shouldOfferForceRemove(lower)) {
                ApplicationManager.getApplication().invokeLater {
                    val dialog = DeleteConfirmationDialog(project, worktree)
                    if (dialog.showAndGet() && dialog.forceRemove) {
                        removeWorktree(root, worktree, true)
                    }
                }
                return@runInBackground
            }

            val folderStillExists = File(worktree.path).exists()
            var shouldRefresh = false
            val friendlyMessage = when {
                lower.contains("failed to delete") || lower.contains("invalid argument") -> {
                    if (folderStillExists) {
                        shouldRefresh = true
                        "Git worktree reference removed, but the folder could not be deleted. " +
                            "Please manually delete: ${worktree.path}"
                    } else {
                        "Cannot remove worktree - files may be in use. Close any open files and try again."
                    }
                }
                else -> errorMessage
            }

            notifications.error(friendlyMessage)
            if (shouldRefresh) {
                refreshWorktrees()
            }
        }
    }

    private fun shouldOfferForceRemove(errorMessage: String): Boolean {
        return errorMessage.contains("modified or untracked files") ||
            errorMessage.contains("contains modified or untracked files") ||
            errorMessage.contains("forcing it") ||
            errorMessage.contains("locked working tree") ||
            errorMessage.contains("remove -f -f") ||
            errorMessage.contains("unlock first")
    }

    private fun canRemove(worktree: Worktree): Boolean {
        return !worktree.isMainWorktree && !isCurrentWorktree(worktree)
    }

    private fun isCurrentWorktree(worktree: Worktree): Boolean {
        val basePath = project.basePath ?: return false
        return try {
            File(basePath).canonicalFile == File(worktree.path).canonicalFile
        } catch (ex: Exception) {
            false
        }
    }

    private fun openProject(path: String) {
        try {
            val openTarget = resolveProjectOpenTarget(path)
            val openedProject = ProjectUtil.openOrImport(openTarget.absolutePath, project, true)
            if (openedProject == null) {
                notifications.error("Failed to open project at: ${openTarget.absolutePath}")
            }
        } catch (ex: Exception) {
            notifications.error("Failed to open project: ${ex.message}")
        }
    }

    private fun resolveProjectOpenTarget(worktreePath: String): File {
        val root = File(worktreePath)
        if (!root.isDirectory) {
            return root
        }

        // Prefer a solution at the root so the common case stays fast.
        // If the root contains any solution files, the root wins (or null when
        // ambiguous) — do not let nested results poison an ambiguous root.
        val rootFiles = enumerateSolutionFiles(root, maxDepth = 0)
        if (rootFiles.isNotEmpty()) {
            return pickSolution(rootFiles) ?: root
        }

        val nestedSolution = pickSolution(enumerateSubdirectorySolutionFiles(root, MAX_SOLUTION_SEARCH_DEPTH))
        return nestedSolution ?: root
    }

    /**
     * Chooses a single solution file from the candidates, preferring `.slnx` over `.sln`
     * when both are present. Returns `null` when the choice is ambiguous (no candidates,
     * or multiple of the preferred kind).
     */
    private fun pickSolution(files: List<File>): File? {
        if (files.isEmpty()) {
            return null
        }

        // Prefer .slnx over .sln when both exist (kept in sync with the VS extension).
        val slnx = files.filter { it.extension.equals("slnx", ignoreCase = true) }
        val candidates = if (slnx.isNotEmpty()) slnx else files
        return candidates.singleOrNull()
    }

    /**
     * Recursively collects `.sln`/`.slnx` files up to [maxDepth] levels below [root],
     * skipping build and tooling directories.
     */
    private fun enumerateSolutionFiles(root: File, maxDepth: Int): List<File> {
        val results = mutableListOf<File>()
        collectSolutionFiles(root, maxDepth, results)
        return results
    }

    private fun enumerateSubdirectorySolutionFiles(root: File, maxDepth: Int): List<File> {
        val results = mutableListOf<File>()
        val entries = root.listFiles() ?: return results
        for (entry in entries) {
            if (!entry.isDirectory) {
                continue
            }
            val name = entry.name
            if (name.startsWith('.') || EXCLUDED_SEARCH_DIRECTORIES.any { it.equals(name, ignoreCase = true) }) {
                continue
            }
            collectSolutionFiles(entry, maxDepth - 1, results)
        }
        return results
    }

    private fun collectSolutionFiles(directory: File, remainingDepth: Int, results: MutableList<File>) {
        val entries = directory.listFiles() ?: return

        for (entry in entries) {
            if (entry.isFile &&
                (entry.extension.equals("sln", ignoreCase = true) ||
                    entry.extension.equals("slnx", ignoreCase = true))
            ) {
                results.add(entry)
            }
        }

        if (remainingDepth <= 0) {
            return
        }

        for (entry in entries) {
            if (!entry.isDirectory) {
                continue
            }
            val name = entry.name
            if (name.startsWith('.') || EXCLUDED_SEARCH_DIRECTORIES.any { it.equals(name, ignoreCase = true) }) {
                continue
            }
            collectSolutionFiles(entry, remainingDepth - 1, results)
        }
    }

    private fun setLoading(isLoading: Boolean) {
        ApplicationManager.getApplication().invokeLater {
            loadingIcon.isVisible = isLoading
            if (isLoading) {
                loadingIcon.resume()
            } else {
                loadingIcon.suspend()
            }
        }
    }

    private fun runInBackground(title: String, action: suspend () -> Unit) {
        ProgressManager.getInstance().run(object : Task.Backgroundable(project, title, false) {
            override fun run(indicator: ProgressIndicator) {
                runBlocking { action() }
            }
        })
    }

    private fun createActionButton(text: String, action: () -> Unit): JButton {
        val button = JButton(text)
        button.isFocusable = false
        button.font = JBUI.Fonts.smallFont()
        button.margin = JBUI.insets(2, 8)
        button.putClientProperty("JButton.buttonType", "toolbar")
        button.addActionListener { action() }
        return button
    }

    private fun createTagLabel(text: String, colors: TagColors): JBLabel {
        val label = JBLabel(text)
        label.font = JBUI.Fonts.smallFont()
        label.foreground = colors.foreground
        label.background = colors.background
        label.isOpaque = true
        label.border = JBUI.Borders.empty(1, 6)
        return label
    }

    private data class TagColors(
        val background: Color,
        val foreground: Color,
    )

    private inner class WorktreeCard(val worktree: Worktree) : JBPanel<WorktreeCard>(BorderLayout()) {
        private val nameLabel = JBLabel()
        private val pathLabel = JBLabel()
        private val branchLabel = JBLabel()
        private val commitLabel = JBLabel()
        private val tagPanel = JPanel(FlowLayout(FlowLayout.LEFT, JBUI.scale(6), 0))
        private val actionPanel = JPanel(FlowLayout(FlowLayout.LEFT, JBUI.scale(6), 0))

        init {
            isOpaque = true
            background = cardBackground
            border = JBUI.Borders.compound(
                JBUI.Borders.customLine(JBColor.border()),
                JBUI.Borders.empty(8),
            )

            nameLabel.icon = AllIcons.Nodes.Folder
            nameLabel.font = JBUI.Fonts.label().deriveFont(Font.BOLD)
            nameLabel.toolTipText = worktree.path

            pathLabel.font = JBUI.Fonts.smallFont()
            pathLabel.foreground = JBColor.lazy { UIUtil.getContextHelpForeground() }

            branchLabel.icon = AllIcons.Vcs.Branch
            branchLabel.font = JBUI.Fonts.smallFont()

            commitLabel.icon = AllIcons.Actions.Commit
            commitLabel.font = JBUI.Fonts.smallFont()

            tagPanel.isOpaque = false
            actionPanel.isOpaque = false

            val headerRow = JPanel()
            headerRow.isOpaque = false
            headerRow.layout = BoxLayout(headerRow, BoxLayout.X_AXIS)
            headerRow.add(nameLabel)
            headerRow.add(Box.createHorizontalStrut(8))
            headerRow.add(tagPanel)
            headerRow.add(Box.createHorizontalGlue())

            val detailRow = JPanel()
            detailRow.isOpaque = false
            detailRow.layout = BoxLayout(detailRow, BoxLayout.X_AXIS)
            detailRow.add(branchLabel)
            detailRow.add(Box.createHorizontalStrut(8))
            detailRow.add(commitLabel)
            detailRow.add(Box.createHorizontalGlue())

            val openButton = createActionButton("Open in IDE") {
                openProject(worktree.path)
            }
            val revealButton = createActionButton("Explorer") {
                RevealFileAction.openDirectory(File(worktree.path))
            }
            val copyButton = createActionButton("Copy Path") {
                CopyPasteManager.getInstance().setContents(StringSelection(worktree.path))
                notifications.info("Copied worktree path.")
            }
            val removeButton = createActionButton("Remove") {
                confirmAndRemove(worktree)
            }
            removeButton.isEnabled = canRemove(worktree)
            if (!removeButton.isEnabled) {
                removeButton.toolTipText = "Main or current worktrees cannot be removed."
            }

            actionPanel.add(openButton)
            actionPanel.add(revealButton)
            actionPanel.add(copyButton)
            actionPanel.add(removeButton)

            val content = JPanel()
            content.isOpaque = false
            content.layout = BoxLayout(content, BoxLayout.Y_AXIS)
            content.add(headerRow)
            content.add(Box.createVerticalStrut(4))
            content.add(pathLabel)
            content.add(Box.createVerticalStrut(4))
            content.add(detailRow)
            content.add(Box.createVerticalStrut(6))
            content.add(actionPanel)

            add(content, BorderLayout.CENTER)
            updateContent()

            alignmentX = Component.LEFT_ALIGNMENT
        }

        override fun getMaximumSize(): Dimension {
            val size = preferredSize
            return Dimension(Int.MAX_VALUE, size.height)
        }

        private fun updateContent() {
            val name = File(worktree.path).name
            val branchLabelText = worktree.branch ?: "(detached)"
            val head = worktree.headCommit.take(7)

            nameLabel.text = name
            pathLabel.text = worktree.path
            branchLabel.text = branchLabelText
            commitLabel.text = head

            tagPanel.removeAll()
            val tags = mutableListOf<Pair<String, TagColors>>()
            if (isCurrentWorktree(worktree)) {
                tags.add("CURRENT" to currentTagColors)
            }
            if (worktree.isMainWorktree) {
                tags.add("MAIN" to mainTagColors)
            }
            if (worktree.isLocked) {
                tags.add("LOCKED" to neutralTagColors)
            }

            tags.forEach { (text, colors) ->
                tagPanel.add(createTagLabel(text, colors))
            }
            tagPanel.isVisible = tags.isNotEmpty()
        }
    }

    private companion object {
        /** Maximum directory depth to search when no solution is found at the worktree root. */
        const val MAX_SOLUTION_SEARCH_DEPTH = 4

        /**
         * Directories skipped while searching for solution files because they never
         * contain a user-authored solution and can be expensive to traverse.
         */
        val EXCLUDED_SEARCH_DIRECTORIES = listOf(
            ".git", "bin", "obj", "node_modules", ".vs", ".vscode", "packages", "TestResults"
        )
    }
}

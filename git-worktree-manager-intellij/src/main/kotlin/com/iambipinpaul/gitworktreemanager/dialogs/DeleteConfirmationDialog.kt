package com.iambipinpaul.gitworktreemanager.dialogs

import com.iambipinpaul.gitworktreemanager.models.Worktree
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.DialogWrapper
import com.intellij.openapi.ui.ValidationInfo
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.JBUI
import java.awt.BorderLayout
import java.io.File
import javax.swing.JComponent
import javax.swing.JPanel

class DeleteConfirmationDialog(
    project: Project,
    private val worktree: Worktree,
    private val locked: Boolean = false,
) : DialogWrapper(project) {
    private val forceCheckBox = JBCheckBox(
        if (locked) "I understand this will override the lock and permanently delete the worktree."
        else "I understand uncommitted changes will be permanently lost."
    )

    val forceRemove: Boolean
        get() = forceCheckBox.isSelected

    init {
        title = "Force Remove Worktree"
        setOKButtonText("Force Remove")
        init()
        initValidation()

        forceCheckBox.addActionListener {
            initValidation()
        }
    }

    override fun createCenterPanel(): JComponent {
        val panel = JPanel(BorderLayout(0, 8))
        panel.border = JBUI.Borders.empty(8)

        val name = File(worktree.path).name
        val message = "You are about to force remove the worktree \"$name\".\nPath: ${worktree.path}"
        panel.add(JBLabel(message), BorderLayout.NORTH)
        panel.add(forceCheckBox, BorderLayout.CENTER)
        return panel
    }

    override fun doValidate(): ValidationInfo? {
        if (!forceCheckBox.isSelected) {
            return ValidationInfo("Confirm the risk to enable force removal.", forceCheckBox)
        }
        return null
    }
}

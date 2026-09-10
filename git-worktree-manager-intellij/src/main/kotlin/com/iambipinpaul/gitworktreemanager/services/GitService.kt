package com.iambipinpaul.gitworktreemanager.services

import com.iambipinpaul.gitworktreemanager.models.GitCommandResult
import com.iambipinpaul.gitworktreemanager.models.Worktree
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.CapturingProcessHandler
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.util.SystemInfo
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

data class WorktreeStatus(
    val modifiedCount: Int,
    val untrackedCount: Int,
    val incoming: Int,
    val outgoing: Int,
    val hasUpstream: Boolean = true,
)

class GitService(
    private val logger: Logger = Logger.getInstance(GitService::class.java),
) {
    suspend fun getWorktrees(repositoryPath: String): GitCommandResult<List<Worktree>> {
        logger.info("Getting worktrees for repository: $repositoryPath")

        val result = executeGitCommand(repositoryPath, listOf("worktree", "list", "--porcelain"))
        if (!result.success) {
            logger.error("Failed to list worktrees: ${result.errorMessage}")
            return GitCommandResult.fail(result.errorMessage ?: "Failed to list worktrees", result.exitCode)
        }

        val worktrees = WorktreeParser.parsePorcelainOutput(result.output ?: "")
        logger.info("Found ${worktrees.size} worktree(s)")
        return GitCommandResult.ok(worktrees)
    }

    suspend fun addWorktree(
        repositoryPath: String,
        worktreePath: String,
        branchName: String,
        createBranch: Boolean = false,
        baseBranch: String? = null,
    ): GitCommandResult<Unit> {
        val args = mutableListOf("worktree", "add")
        if (createBranch) {
            args.addAll(listOf("-b", branchName))
            args.add(worktreePath)
            if (!baseBranch.isNullOrBlank()) {
                args.add(baseBranch)
            }
        } else {
            args.add(worktreePath)
            args.add(branchName)
        }

        logger.info(
            "Adding worktree: path='$worktreePath', branch='$branchName', " +
                "createBranch=$createBranch, baseBranch='$baseBranch'",
        )

        val result = executeGitCommand(repositoryPath, args)
        return if (result.success) {
            logger.info("Successfully added worktree at '$worktreePath'")
            GitCommandResult.ok()
        } else {
            logger.error("Failed to add worktree: ${result.errorMessage}")
            GitCommandResult.fail(result.errorMessage ?: "Failed to add worktree", result.exitCode)
        }
    }

    suspend fun removeWorktree(
        repositoryPath: String,
        worktreePath: String,
        force: Boolean = false,
    ): GitCommandResult<Unit> {
        // A single --force removes dirty worktrees, but locked worktrees require
        // -f -f, so send --force twice whenever force is requested.
        val args = mutableListOf("worktree", "remove")
        if (force) {
            args.add("--force")
            args.add("--force")
        }
        args.add(worktreePath)

        logger.info("Removing worktree: path='$worktreePath', force=$force")

        val result = executeGitCommand(repositoryPath, args)
        return if (result.success) {
            logger.info("Successfully removed worktree at '$worktreePath'")
            GitCommandResult.ok()
        } else {
            logger.error("Failed to remove worktree: ${result.errorMessage}")
            GitCommandResult.fail(result.errorMessage ?: "Failed to remove worktree", result.exitCode)
        }
    }

    suspend fun getRepositoryRoot(path: String): String? {
        logger.info("Getting repository root for path: $path")

        val result = executeGitCommand(path, listOf("rev-parse", "--show-toplevel"))
        if (!result.success || result.output.isNullOrBlank()) {
            logger.warn("Path '$path' is not within a Git repository")
            return null
        }

        val root = result.output.trim()
        logger.info("Repository root: $root")
        return root
    }

    suspend fun isGitInstalled(): Boolean {
        logger.info("Checking if Git is installed")

        return try {
            val result = executeGitCommand(File(".").absolutePath, listOf("--version"))
            if (result.success) {
                logger.info("Git is installed: ${result.output?.trim()}")
            } else {
                logger.warn("Git is not installed or not in PATH")
            }
            result.success
        } catch (ex: Exception) {
            logger.error("Error checking Git installation", ex)
            false
        }
    }

    suspend fun getBranches(repositoryPath: String): GitCommandResult<List<String>> {
        logger.info("Getting branches for repository: $repositoryPath")

        val localResult = executeGitCommand(
            repositoryPath,
            listOf("branch", "--format=%(refname:short)"),
        )
        val remoteResult = executeGitCommand(
            repositoryPath,
            listOf("branch", "-r", "--format=%(refname:short)"),
        )

        val branches = mutableListOf<String>()

        if (localResult.success && !localResult.output.isNullOrBlank()) {
            val localBranches = localResult.output
                .lineSequence()
                .map { it.trim() }
                .filter { it.isNotEmpty() }
            branches.addAll(localBranches)
        }

        if (remoteResult.success && !remoteResult.output.isNullOrBlank()) {
            val remoteBranches = remoteResult.output
                .lineSequence()
                .map { it.trim() }
                .filter { it.isNotEmpty() && !it.contains("HEAD") }
                .map { if (it.startsWith("origin/")) it.substring(7) else it }
                .filter { it !in branches }
            branches.addAll(remoteBranches)
        }

        logger.info("Found ${branches.size} branch(es)")
        return GitCommandResult.ok(branches.distinct().sorted())
    }

    suspend fun getWorktreeStatus(path: String, branch: String): WorktreeStatus {
        try {
            if (path.isBlank() || !File(path).exists()) {
                return WorktreeStatus(0, 0, 0, 0, false)
            }

            val statusResult = executeGitCommand(
                path,
                listOf("status", "--porcelain=v1", "-unormal"),
                timeoutMs = STATUS_TIMEOUT_MS,
            )

            var modified = 0
            var untracked = 0

            if (statusResult.success && !statusResult.output.isNullOrBlank()) {
                for (line in statusResult.output.lineSequence()) {
                    if (line.length < 3) {
                        continue
                    }
                    val status = line.substring(0, 2)
                    if (status == "??") {
                        untracked++
                    } else {
                        modified++
                    }
                }
            }

            var ahead = 0
            var behind = 0
            var hasUpstream = false

            try {
                val upstreamResult = executeGitCommand(
                    path,
                    listOf("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"),
                    timeoutMs = UPSTREAM_TIMEOUT_MS,
                )

                if (upstreamResult.success && !upstreamResult.output.isNullOrBlank()) {
                    hasUpstream = true
                    val countResult = executeGitCommand(
                        path,
                        listOf("rev-list", "--left-right", "--count", "HEAD...@{u}"),
                        timeoutMs = UPSTREAM_TIMEOUT_MS,
                    )

                    if (countResult.success && !countResult.output.isNullOrBlank()) {
                        val parts = countResult.output.trim()
                            .split(Regex("\\s+"))
                            .filter { it.isNotBlank() }
                        if (parts.size >= 2) {
                            ahead = parts[0].toIntOrNull() ?: 0
                            behind = parts[1].toIntOrNull() ?: 0
                        }
                    }
                }
            } catch (ex: Exception) {
                logger.warn("No upstream found for $path. Branch is local-only.")
                hasUpstream = false
            }

            return WorktreeStatus(modified, untracked, behind, ahead, hasUpstream)
        } catch (ex: CancellationException) {
            logger.warn("Status check cancelled for worktree: $path (branch=$branch)")
            return WorktreeStatus(0, 0, 0, 0, false)
        } catch (ex: Exception) {
            logger.error("Failed to get status for $path", ex)
            return WorktreeStatus(0, 0, 0, 0, false)
        }
    }

    private suspend fun executeGitCommand(
        workingDirectory: String,
        arguments: List<String>,
        timeoutMs: Int = DEFAULT_TIMEOUT_MS,
    ): GitProcessResult {
        val enableLongPathSupport = SystemInfo.isWindows
        val gitArguments = buildGitArguments(arguments, enableLongPathSupport)

        logger.info("Executing: git ${gitArguments.joinToString(" ")} (in $workingDirectory)")

        val commandLine = GeneralCommandLine(GIT_EXECUTABLE)
            .withWorkDirectory(workingDirectory)
            .withCharset(Charsets.UTF_8)
            .withParameters(gitArguments)

        val handler = CapturingProcessHandler(commandLine)

        return withContext(Dispatchers.IO) {
            try {
                val output = handler.runProcess(timeoutMs)
                if (output.isTimeout) {
                    handler.destroyProcess()
                    logger.error("Git command timed out after ${timeoutMs}ms: git ${gitArguments.joinToString(" ")}")
                    return@withContext GitProcessResult(
                        success = false,
                        exitCode = -1,
                        output = null,
                        errorMessage = "Git command timed out",
                    )
                }

                val exitCode = output.exitCode
                val stdout = output.stdout
                val stderr = output.stderr

                if (stdout.isNotBlank()) {
                    logger.info("Git stdout:\n${stdout.trim()}")
                }
                if (stderr.isNotBlank()) {
                    if (exitCode == 0) {
                        logger.warn("Git stderr (exit code 0):\n${stderr.trim()}")
                    } else {
                        logger.error("Git stderr (exit code $exitCode):\n${stderr.trim()}")
                    }
                }

                logger.info("Git command completed with exit code: $exitCode")

                GitProcessResult(
                    success = exitCode == 0,
                    exitCode = exitCode,
                    output = stdout,
                    errorMessage = if (exitCode != 0) {
                        createUserFacingErrorMessage(
                            stderr.trim().ifBlank { "Git command failed with exit code $exitCode." },
                            enableLongPathSupport,
                        )
                    } else {
                        null
                    },
                )
            } catch (ex: CancellationException) {
                handler.destroyProcess()
                logger.warn("Git command was cancelled: git ${gitArguments.joinToString(" ")}")
                GitProcessResult(
                    success = false,
                    exitCode = -1,
                    output = null,
                    errorMessage = "Git command was cancelled",
                )
            } catch (ex: Exception) {
                logger.error("Failed to execute Git command: git ${gitArguments.joinToString(" ")}", ex)
                GitProcessResult(
                    success = false,
                    exitCode = -1,
                    output = null,
                    errorMessage = createUserFacingErrorMessage(
                        "Failed to execute Git command: ${ex.message}",
                        enableLongPathSupport,
                    ),
                )
            }
        }
    }

    private data class GitProcessResult(
        val success: Boolean,
        val exitCode: Int,
        val output: String?,
        val errorMessage: String?,
    )

    companion object {
        private const val DEFAULT_TIMEOUT_MS = 30_000
        private const val STATUS_TIMEOUT_MS = 10_000
        private const val UPSTREAM_TIMEOUT_MS = 5_000
        private const val GIT_EXECUTABLE = "git"
        private val LONG_PATH_ERROR_PATTERNS = listOf(
            "filename too long",
            "file name too long",
            "path too long",
            "path length",
            "path, file name, or both are too long",
        )

        internal fun buildGitArguments(
            arguments: List<String>,
            enableLongPathSupport: Boolean,
        ): List<String> {
            return if (enableLongPathSupport) {
                listOf("-c", "core.longpaths=true") + arguments
            } else {
                arguments
            }
        }

        internal fun isLongPathError(errorMessage: String?): Boolean {
            if (errorMessage.isNullOrBlank()) {
                return false
            }

            return LONG_PATH_ERROR_PATTERNS.any { pattern ->
                errorMessage.contains(pattern, ignoreCase = true)
            }
        }

        internal fun createUserFacingErrorMessage(
            errorMessage: String,
            longPathSupportWasEnabled: Boolean,
        ): String {
            if (!isLongPathError(errorMessage)) {
                return errorMessage
            }

            val supportStatus = if (longPathSupportWasEnabled) {
                "Git Worktree Hub already ran this Git command with process-scoped core.longpaths=true."
            } else {
                "This looks like a Git for Windows long path error."
            }

            return listOf(
                errorMessage,
                supportStatus +
                    " If the same repository also fails from a terminal or another Git tool, " +
                    "enable Git for Windows long paths globally with:",
                "git config --global core.longpaths true",
                "This is a Git for Windows setting, not a JetBrains IDE setting.",
            ).joinToString(System.lineSeparator() + System.lineSeparator())
        }
    }
}

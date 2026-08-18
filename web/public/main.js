(() => {
	"use strict";

	const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

	/* Floating tubelight navigation with active-section tracking. */
	const nav = document.getElementById("nav");
	const navigationLinks = [...document.querySelectorAll("[data-nav-link]")];
	const navigationSections = navigationLinks
		.map((link) => ({ link, section: document.getElementById(link.dataset.navTarget) }))
		.filter((item) => item.section);

	const setActiveNavigation = (activeLink) => {
		navigationLinks.forEach((link) => {
			const active = link === activeLink;
			link.classList.toggle("is-active", active);
			if (active) link.setAttribute("aria-current", "page");
			else link.removeAttribute("aria-current");
		});
	};

	let navigationFrame = 0;
	const updateNavigation = () => {
		navigationFrame = 0;
		nav?.classList.toggle("is-stuck", window.scrollY > 8);
		const marker = window.scrollY + window.innerHeight * 0.36;
		let active = navigationSections[0];
		for (const item of navigationSections) {
			if (item.section.offsetTop <= marker) active = item;
		}
		if (active) setActiveNavigation(active.link);
	};

	const scheduleNavigationUpdate = () => {
		if (navigationFrame) return;
		navigationFrame = window.requestAnimationFrame(updateNavigation);
	};

	navigationLinks.forEach((link) => link.addEventListener("click", () => setActiveNavigation(link)));
	updateNavigation();
	window.addEventListener("scroll", scheduleNavigationUpdate, { passive: true });
	window.addEventListener("resize", scheduleNavigationUpdate);

	/* Reveal content only after it is ready. The timeout prevents hidden content in unusual browsers. */
	const revealables = [...document.querySelectorAll(".reveal")];
	const showAll = () => revealables.forEach((element) => element.classList.add("is-in"));
	window.setTimeout(showAll, 2200);

	if (reducedMotion || !("IntersectionObserver" in window)) {
		showAll();
	} else {
		const observer = new IntersectionObserver((entries) => {
			entries.forEach((entry) => {
				if (!entry.isIntersecting) return;
				const siblings = [...entry.target.parentElement.children].filter((element) => element.classList.contains("reveal"));
				const index = Math.max(0, siblings.indexOf(entry.target));
				entry.target.style.transitionDelay = `${Math.min(index * 65, 325)}ms`;
				entry.target.classList.add("is-in");
				observer.unobserve(entry.target);
			});
		}, { rootMargin: "0px 0px -7% 0px", threshold: 0.1 });
		revealables.forEach((element) => observer.observe(element));
	}

	/* Interactive hero: a compact demonstration of one complete setup switch. */
	const heroDemo = document.getElementById("heroDemo");
	const deskButton = document.getElementById("pillDesk");
	const simButton = document.getElementById("pillSim");
	const modeName = heroDemo?.querySelector("[data-mode-name]");
	const displayCount = heroDemo?.querySelector("[data-display-count]");
	const audioName = heroDemo?.querySelector("[data-audio-name]");
	const windowCount = heroDemo?.querySelector("[data-window-count]");
	let heroTimer;
	let heroAutomatic = true;

	const modes = {
		desk: { name: "Desk", displays: "2 active", audio: "Headphones", windows: "6 restored" },
		sim: { name: "Sim Racing", displays: "1 active", audio: "Rig speakers", windows: "3 restored" }
	};

	const setHeroMode = (mode) => {
		if (!heroDemo || !modes[mode]) return;
		heroDemo.classList.add("is-switching");
		heroDemo.dataset.mode = mode;
		deskButton?.classList.toggle("is-on", mode === "desk");
		simButton?.classList.toggle("is-on", mode === "sim");
		deskButton?.setAttribute("aria-pressed", String(mode === "desk"));
		simButton?.setAttribute("aria-pressed", String(mode === "sim"));
		window.setTimeout(() => {
			const content = modes[mode];
			if (modeName) modeName.textContent = content.name;
			if (displayCount) displayCount.textContent = content.displays;
			if (audioName) audioName.textContent = content.audio;
			if (windowCount) windowCount.textContent = content.windows;
			heroDemo.classList.remove("is-switching");
		}, reducedMotion ? 0 : 260);
	};

	const scheduleHeroSwitch = () => {
		window.clearTimeout(heroTimer);
		if (!heroAutomatic || reducedMotion || document.hidden) return;
		heroTimer = window.setTimeout(() => {
			setHeroMode(heroDemo?.dataset.mode === "desk" ? "sim" : "desk");
			scheduleHeroSwitch();
		}, 4200);
	};

	const chooseHeroMode = (mode) => {
		heroAutomatic = false;
		window.clearTimeout(heroTimer);
		setHeroMode(mode);
	};

	if (heroDemo && deskButton && simButton) {
		deskButton.addEventListener("click", () => chooseHeroMode("desk"));
		simButton.addEventListener("click", () => chooseHeroMode("sim"));
		setHeroMode("desk");
		scheduleHeroSwitch();
		document.addEventListener("visibilitychange", scheduleHeroSwitch);
	}

	/* Full front-end app preview. It mirrors the product without touching the visitor's PC. */
	const demoApp = document.querySelector("[data-demo-app]");
	if (demoApp) {
		const demoWindow = demoApp.querySelector(".preview-window");
		const appNavigation = [...demoApp.querySelectorAll("[data-app-view-target]")];
		const appViews = [...demoApp.querySelectorAll("[data-app-view]")];
		const setupCards = [...demoApp.querySelectorAll("[data-setup-card]")];
		const detailTabs = [...demoApp.querySelectorAll("[data-detail-tab]")];
		const detailPanels = [...demoApp.querySelectorAll("[data-detail-panel]")];
		const toast = demoApp.querySelector(".preview-toast");
		let activeSetup = "desk";
		let detailSetup = "desk";
		let toastTimer;
		let switchingTimer;

		const profiles = {
			desk: {
				name: "Desk",
				meta: "2 displays · 6 windows",
				displayCount: "2",
				displayLabel: "2 active",
				monitors: "desk",
				primaryName: "M27Q",
				primaryResolution: "2560 × 1440",
				displayOne: "G24F 2",
				displayOneSpec: "1920 × 1080 165 Hz",
				displayTwo: "M27Q (primary)",
				displayTwoSpec: "2560 × 1440 170 Hz",
				audio: "Headphones",
				automation: "Return to Desk when the racing session closes.",
				hotkey: "Ctrl + Alt + 1",
				apps: "No applications configured for this setup."
			},
			sim: {
				name: "Sim Racing",
				meta: "1 display · 3 apps",
				displayCount: "1",
				displayLabel: "1 active",
				monitors: "sim",
				primaryName: "Samsung G5",
				primaryResolution: "3440 × 1440",
				displayOne: "Samsung G5 (primary)",
				displayOneSpec: "3440 × 1440 165 Hz",
				displayTwo: "Desk displays",
				displayTwoSpec: "Off while racing",
				audio: "Speakers (SW5 Dongle)",
				automation: "Move to the rig when Assetto Corsa Competizione starts.",
				hotkey: "Ctrl + Alt + 2",
				apps: "Launch CrewChief and SimHub with this setup."
			}
		};

		const showDemoToast = (message) => {
			if (!toast) return;
			window.clearTimeout(toastTimer);
			toast.textContent = message;
			toast.classList.add("is-on");
			toastTimer = window.setTimeout(() => toast.classList.remove("is-on"), 3200);
		};

		const showAppView = (viewName) => {
			appViews.forEach((view) => {
				const active = view.dataset.appView === viewName;
				view.classList.toggle("is-on", active);
				view.hidden = !active;
			});
			const navigationView = viewName === "detail" ? "setups" : viewName;
			appNavigation.forEach((button) => {
				const active = button.dataset.appViewTarget === navigationView;
				button.classList.toggle("is-on", active);
				if (active) button.setAttribute("aria-current", "page");
				else button.removeAttribute("aria-current");
			});
		};

		const updateDetail = (setup) => {
			const profile = profiles[setup];
			if (!profile) return;
			detailSetup = setup;
			const textFields = {
				"[data-detail-name]": profile.name,
				"[data-detail-meta]": profile.meta,
				"[data-detail-display-label]": profile.displayLabel,
				"[data-detail-primary-name]": profile.primaryName,
				"[data-detail-primary-resolution]": profile.primaryResolution,
				"[data-detail-display-one]": profile.displayOne,
				"[data-detail-display-one-spec]": profile.displayOneSpec,
				"[data-detail-display-two]": profile.displayTwo,
				"[data-detail-display-two-spec]": profile.displayTwoSpec,
				"[data-detail-audio]": profile.audio,
				"[data-detail-automation]": profile.automation,
				"[data-detail-hotkey]": profile.hotkey,
				"[data-detail-apps]": profile.apps
			};
			Object.entries(textFields).forEach(([selector, value]) => {
				const element = demoApp.querySelector(selector);
				if (element) element.textContent = value;
			});
			const monitorStage = demoApp.querySelector("[data-detail-monitors]");
			if (monitorStage) monitorStage.dataset.detailMonitors = profile.monitors;
			const identityName = demoApp.querySelector("[data-detail-identity-name]");
			const identityType = demoApp.querySelector("[data-detail-identity-type]");
			if (identityName) identityName.value = profile.name;
			if (identityType) identityType.value = setup === "desk" ? "Desk" : "Sim Racing";
			const activeBadge = demoApp.querySelector("[data-detail-active]");
			if (activeBadge) activeBadge.hidden = setup !== activeSetup;
			const detailApply = demoApp.querySelector("[data-detail-apply]");
			if (detailApply) detailApply.textContent = setup === activeSetup ? "Reapply setup" : "Switch to setup";
		};

		const setActiveSetup = (setup) => {
			const profile = profiles[setup];
			if (!profile) return;
			activeSetup = setup;
			demoApp.dataset.activeSetup = setup;
			setupCards.forEach((card) => {
				const active = card.dataset.setupCard === setup;
				card.classList.toggle("is-active", active);
				const state = card.querySelector("[data-setup-state]");
				const stateLabel = card.querySelector("[data-setup-state-label]");
				const apply = card.querySelector("[data-demo-apply]");
				if (state) state.textContent = active ? "Active" : "Ready";
				if (stateLabel) stateLabel.textContent = active ? "Applied" : "Available";
				if (apply) apply.textContent = active ? "Reapply" : "Switch";
			});
			demoApp.querySelectorAll("[data-demo-active-name]").forEach((element) => { element.textContent = profile.name; });
			demoApp.querySelectorAll("[data-demo-active-meta]").forEach((element) => { element.textContent = profile.meta; });
			demoApp.querySelectorAll("[data-demo-display-count]").forEach((element) => { element.textContent = profile.displayCount; });
			updateDetail(detailSetup);
		};

		const applySetup = (setup) => {
			if (!profiles[setup] || !demoWindow) return;
			window.clearTimeout(switchingTimer);
			demoWindow.classList.add("is-switching");
			switchingTimer = window.setTimeout(() => {
				setActiveSetup(setup);
				demoWindow.classList.remove("is-switching");
				showDemoToast(`${profiles[setup].name} is now active in the preview — no PC changes were made.`);
			}, reducedMotion ? 0 : 950);
		};

		appNavigation.forEach((button) => button.addEventListener("click", () => showAppView(button.dataset.appViewTarget)));
		demoApp.querySelectorAll("[data-open-detail]").forEach((button) => {
			button.addEventListener("click", () => {
				updateDetail(button.dataset.openDetail);
				showAppView("detail");
			});
		});
		demoApp.querySelector("[data-app-back]")?.addEventListener("click", () => showAppView("setups"));
		demoApp.querySelectorAll("[data-demo-apply]").forEach((button) => button.addEventListener("click", () => applySetup(button.dataset.demoApply)));
		demoApp.querySelector("[data-detail-apply]")?.addEventListener("click", () => applySetup(detailSetup));

		const selectDetailTab = (selected, moveFocus = false) => {
			const name = selected.dataset.detailTab;
			detailTabs.forEach((tab) => {
				const active = tab === selected;
				tab.classList.toggle("is-on", active);
				tab.setAttribute("aria-selected", String(active));
				tab.tabIndex = active ? 0 : -1;
			});
			detailPanels.forEach((panel) => {
				const active = panel.dataset.detailPanel === name;
				panel.classList.toggle("is-on", active);
				panel.hidden = !active;
			});
			if (moveFocus) selected.focus();
		};
		detailTabs.forEach((tab, index) => {
			tab.addEventListener("click", () => selectDetailTab(tab));
			tab.addEventListener("keydown", (event) => {
				if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
				event.preventDefault();
				let nextIndex = event.key === "Home" ? 0 : event.key === "End" ? detailTabs.length - 1 : event.key === "ArrowRight" ? (index + 1) % detailTabs.length : (index - 1 + detailTabs.length) % detailTabs.length;
				selectDetailTab(detailTabs[nextIndex], true);
			});
		});

		demoApp.querySelectorAll("[data-demo-toggle]").forEach((toggle) => {
			toggle.addEventListener("click", () => {
				const active = toggle.getAttribute("aria-pressed") !== "true";
				toggle.setAttribute("aria-pressed", String(active));
				toggle.classList.toggle("is-on", active);
				showDemoToast("Preview setting changed — your PC was not affected.");
			});
		});
		demoApp.querySelectorAll("[data-demo-action]").forEach((button) => button.addEventListener("click", () => showDemoToast(button.dataset.demoAction)));
		setActiveSetup("desk");
	}

	/* Comparison cards focus a column without hiding any of the fair context. */
	const comparison = document.querySelector("[data-comparison]");
	if (comparison) {
		const plans = [...comparison.querySelectorAll("[data-compare-plan]")];
		const columns = [...comparison.querySelectorAll("[data-compare-column]")];
		const focusPlan = (name) => {
			plans.forEach((plan) => {
				const selected = plan.dataset.comparePlan === name;
				plan.classList.toggle("is-selected", selected);
				plan.setAttribute("aria-selected", String(selected));
			});
			columns.forEach((cell) => cell.classList.toggle("is-focused", cell.dataset.compareColumn === name));
		};
		plans.forEach((plan) => plan.addEventListener("click", () => focusPlan(plan.dataset.comparePlan)));
		focusPlan("pitlaunch");
	}

	/* Keep the FAQ compact: opening one answer closes the previous one. */
	const accordions = [...document.querySelectorAll("[data-accordion]")];
	accordions.forEach((accordion) => {
		const items = [...accordion.querySelectorAll("details")];
		items.forEach((item) => {
			item.addEventListener("toggle", () => {
				if (!item.open) return;
				items.forEach((other) => {
					if (other !== item) other.open = false;
				});
			});
		});
	});

	/* Category tabs keep the FAQ short while retaining every useful answer. */
	const faqTabs = [...document.querySelectorAll("[data-faq-tab]")];
	const faqPanels = [...document.querySelectorAll("[data-faq-panel]")];
	const selectFaqCategory = (selected, moveFocus = false) => {
		const category = selected.dataset.faqTab;
		faqTabs.forEach((tab) => {
			const active = tab === selected;
			tab.classList.toggle("is-on", active);
			tab.setAttribute("aria-selected", String(active));
			tab.tabIndex = active ? 0 : -1;
		});
		faqPanels.forEach((panel) => {
			const active = panel.dataset.faqPanel === category;
			panel.classList.toggle("is-on", active);
			panel.hidden = !active;
		});
		if (moveFocus) selected.focus();
	};
	faqTabs.forEach((tab, index) => {
		tab.addEventListener("click", () => selectFaqCategory(tab));
		tab.addEventListener("keydown", (event) => {
			let nextIndex = index;
			if (event.key === "ArrowRight") nextIndex = (index + 1) % faqTabs.length;
			else if (event.key === "ArrowLeft") nextIndex = (index - 1 + faqTabs.length) % faqTabs.length;
			else if (event.key === "Home") nextIndex = 0;
			else if (event.key === "End") nextIndex = faqTabs.length - 1;
			else return;
			event.preventDefault();
			selectFaqCategory(faqTabs[nextIndex], true);
		});
	});

	/* Lightweight version of the animated social-links component for this static site. */
	const contactLinks = [...document.querySelectorAll("[data-contact-link]")];
	const contactStatus = document.querySelector("[data-contact-status]");
	contactLinks.forEach((link) => {
		link.addEventListener("click", async (event) => {
			link.classList.remove("is-clicked");
			window.requestAnimationFrame(() => link.classList.add("is-clicked"));
			window.setTimeout(() => link.classList.remove("is-clicked"), 320);

			const value = link.dataset.copyContact;
			if (!value) return;
			event.preventDefault();
			try {
				await navigator.clipboard.writeText(value);
				if (contactStatus) contactStatus.textContent = `Copied ${value} — paste it into Discord.`;
			} catch {
				if (contactStatus) contactStatus.textContent = `Discord username: ${value}`;
			}
		});
	});

	document.querySelectorAll("[data-year]").forEach((element) => {
		element.textContent = new Date().getFullYear();
	});

	/*
	 * Report download-button presses to the Google Analytics tag loaded in the page head.
	 * gtag sends these with sendBeacon, so the event survives the navigation to GitHub.
	 * Nothing here is required for the button to work: if the tag is blocked, the click still
	 * follows the link.
	 */
	document.querySelectorAll("a[data-download]").forEach((link) => {
		link.addEventListener("click", () => {
			if (typeof window.gtag !== "function") return;
			const url = link.getAttribute("href") || "";
			window.gtag("event", "download_click", {
				asset: /apps\.microsoft\.com/i.test(url)
					? "microsoft-store"
					: /Portable/i.test(url) ? "portable-zip" : "installer-exe",
				placement: link.dataset.downloadPlacement || "unknown",
				link_url: url
			});
		});
	});

	/* Use the release API only to improve the static version label; the page works without it. */
	const versionElements = document.querySelectorAll("[data-version]");
	if (versionElements.length) {
		fetch("https://api.github.com/repos/Cevzom/PitLaunch/releases/latest", {
			headers: { Accept: "application/vnd.github+json" }
		})
			.then((response) => response.ok ? response.json() : Promise.reject(response.status))
			.then((release) => {
				if (typeof release?.tag_name !== "string") return;
				const version = release.tag_name.replace(/^v/, "");
				versionElements.forEach((element) => { element.textContent = `PitLaunch ${version}`; });
			})
			.catch(() => { /* Keep the static version fallback. */ });
	}
})();
